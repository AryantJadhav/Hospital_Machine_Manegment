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
        path,
        licence = status.Licence is null
            ? null
            : new
            {
                id = status.Licence.LicenceId,
                hospitalName = status.Licence.HospitalName,
                issuedOn = status.Licence.IssuedOn,
                expiresOn = status.Licence.ExpiresOn,
                modules = status.Licence.Modules,
                maxEquipment = status.Licence.MaxEquipment,
                notes = status.Licence.Notes,
            },
    };
}
