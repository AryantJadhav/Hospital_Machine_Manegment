using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Licensing;
using HospitalPm.Infrastructure.Licensing;

namespace HospitalPm.Api.Operations;

public sealed record InstallLicenceRequest(string Licence);

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
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        group.MapGet("/", Current);
        group.MapPost("/", Install);

        // What every signed-in person needs to know, and nothing more: a ward
        // technician is told that recording is refused and why, without being
        // shown the licence itself.
        app.MapGet("/api/licence/banner", Banner)
            .WithTags("Licence")
            .RequireAuthorization();
    }

    /// <summary>
    /// Days before the end date at which administrators start being told. The
    /// rest of the staff hear nothing until it has actually run out.
    /// </summary>
    private const int WarnAdministratorsDays = 14;

    private static IResult Banner(LicenceService licences, TimeProvider clock, System.Security.Claims.ClaimsPrincipal user)
    {
        var status = licences.Current();
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

        var daysLeft = status.EffectiveExpiry is { } end ? end.DayNumber - today.DayNumber : (int?)null;

        var show = status.State switch
        {
            LicenceState.ReadOnly or LicenceState.Expired => true,
            LicenceState.Valid => daysLeft is <= WarnAdministratorsDays
                                  && user.IsInRole(HospitalPm.Domain.Identity.Roles.Admin),
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
