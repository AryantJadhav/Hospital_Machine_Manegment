using HospitalPm.Api.Auth;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Licensing;
using HospitalPm.Infrastructure.Licensing;

namespace HospitalPm.Api.Operations;

public sealed record InstallLicenceRequest(string Licence);

public sealed record EnterCodeRequest(string? Code);

/// <summary>
/// Licence status, and installing one.
///
/// Nothing in this file blocks anything. A hospital whose licence lapsed still
/// needs the ventilator's service history at two in the morning, and a
/// maintenance system that locks its own users out over billing is one that
/// gets ripped out at renewal. Expiry is a conversation, not a kill switch.
/// </summary>
public static class LicenceEndpoints
{
    public static void MapLicenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/licence")
            .WithTags("Licence")
            .RequirePermission(Permissions.SystemLicence);

        group.MapGet("/", Current);
        group.MapPost("/", Install);

        // What every signed-in person needs to know, and nothing more: a ward
        // technician is told that recording is refused and why, without being
        // shown the licence itself.
        app.MapGet("/api/licence/banner", Banner)
            .WithTags("Licence")
            .RequireAuthorization();

        // The lock screen has nobody signed in, so these two are open. Neither gives anything away: the first says
        // whether this installation is locked and who it is for, and the second does nothing unless the code was
        // signed by us for this licence and is newer than the last.
        app.MapGet("/api/licence/lock", LockStatus)
            .WithTags("Licence")
            .AllowAnonymous();
        app.MapPost("/api/licence/code", EnterCode)
            .WithTags("Licence")
            .AllowAnonymous();
    }

    /// <summary>The longest code accepted. A real one is about a kilobyte; this stops anyone sending a megabyte for nothing.</summary>
    private const int LongestCode = 8 * 1024;

    private static IResult LockStatus(LicenceService licences)
    {
        var state = licences.CurrentLock();
        return Results.Ok(new
        {
            locked = state is not null,
            hospitalName = state?.HospitalName,
            licenceId = state?.LicenceId,
            message = state is null
                ? null
                : $"This installation of Hospital PM{(string.IsNullOrWhiteSpace(state.HospitalName) ? string.Empty : " for " + state.HospitalName)} "
                  + "has been locked by your supplier. Contact them, quoting the licence id below, and they will give you an unlock code.",
        });
    }

    private static IResult EnterCode(EnterCodeRequest request, LicenceService licences)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return Results.BadRequest(new { error = "Paste the code, from the first line to the last." });
        }

        if (request.Code.Length > LongestCode)
        {
            return Results.BadRequest(new { error = "That is too long to be a code. Paste only the code." });
        }

        var result = licences.ApplyCode(request.Code);
        return result.Applied
            ? Results.Ok(new { applied = true, locked = result.Command?.Action == HospitalPm.Domain.Licensing.LicenceAction.Lock, message = result.Message })
            : Results.BadRequest(new { error = result.Message });
    }

    /// <summary>
    /// Days before the end date at which administrators start being told. The
    /// rest of the staff hear nothing until it has actually run out.
    /// </summary>
    private const int WarnAdministratorsDays = 14;

    private static async Task<IResult> Banner(
        LicenceService licences, TimeProvider clock, System.Security.Claims.ClaimsPrincipal user,
        HospitalPm.Api.Auth.PermissionService permissions, CancellationToken ct)
    {
        var mayManageLicence = await permissions.CanAsync(user, HospitalPm.Domain.Identity.Permissions.SystemLicence, ct);

        var status = licences.Current();
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var daysLeft = status.EffectiveExpiry is { } end ? end.DayNumber - today.DayNumber : (int?)null;

        var show = status.State switch
        {
            LicenceState.ReadOnly or LicenceState.Expired => true,
            LicenceState.Valid => daysLeft is <= WarnAdministratorsDays
                                  && mayManageLicence,
            _ => false,
        };

        return Results.Ok(new
        {
            show,
            state = status.State,
            readOnly = status.IsReadOnly,
            message = status.Message,
            expiresOn = status.EffectiveExpiry,
            readOnlyFrom = status.ReadOnlyFrom,
            daysLeft,
        });
    }

    private static IResult Current(LicenceService licences)
        => Results.Ok(Describe(licences.Current(), licences.ResolvePath()));

    private static IResult Install(InstallLicenceRequest request, LicenceService licences)
    {
        if (string.IsNullOrWhiteSpace(request.Licence))
        {
            return Results.BadRequest(new { error = "Paste the licence file's contents." });
        }

        var (saved, status) = licences.Install(request.Licence);

        // A licence that does not verify is a 400 with the reason, not a
        // silent failure — the administrator is standing there waiting.
        return saved
            ? Results.Ok(Describe(status, licences.ResolvePath()))
            : Results.BadRequest(new { error = status.Message });
    }

    private static object Describe(LicenceStatus status, string path) => new
    {
        state = status.State,
        message = status.Message,
        effectiveExpiry = status.EffectiveExpiry,
        readOnlyFrom = status.ReadOnlyFrom,
        path,
        licence = status.Licence is null
            ? null
            : new
            {
                id = status.Licence.LicenceId,
                hospitalName = status.Licence.HospitalName,
                issuedOn = status.Licence.IssuedOn,
                expiresOn = status.Licence.ExpiresOn,
                durationDays = status.Licence.DurationDays,
                modules = status.Licence.Modules,
                maxEquipment = status.Licence.MaxEquipment,
                notes = status.Licence.Notes,
            },
    };
}
