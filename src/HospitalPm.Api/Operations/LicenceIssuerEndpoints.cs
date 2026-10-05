using HospitalPm.Api.Auth;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Licensing;
using HospitalPm.Infrastructure.Licensing;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Operations;

/// <summary>
/// The Developer's licence section: make a licence for a hospital, send it again, and lock or unlock an
/// installation with a signed code.
///
/// Held by the Developer alone, and it only works on the copy of the software that has the signing key. A
/// hospital's installation has none, so every route here that needs to sign answers "no signing key" rather than
/// pretending, and the page says why.
///
/// A lock or unlock is a code to be sent and entered at the hospital. Nothing here reaches into an installation:
/// there is no connection to one, and the code works offline, the same way a licence does.
/// </summary>
public static class LicenceIssuerEndpoints
{
    public static void MapLicenceIssuerEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/developer/licences")
            .WithTags("Licence issuing")
            .RequirePermission(Permissions.LicenceIssue);

        group.MapGet("/", ListAsync);
        group.MapPost("/", IssueAsync);
        group.MapGet("/{id:int}/file", FileAsync);
        group.MapPost("/{id:int}/lock", (int id, LicenceIssuer issuer, ILoggerFactory loggers, System.Security.Claims.ClaimsPrincipal who, HttpContext http, CancellationToken ct) =>
            CommandAsync(id, LicenceAction.Lock, issuer, loggers, who, http, ct));
        group.MapPost("/{id:int}/unlock", (int id, LicenceIssuer issuer, ILoggerFactory loggers, System.Security.Claims.ClaimsPrincipal who, HttpContext http, CancellationToken ct) =>
            CommandAsync(id, LicenceAction.Unlock, issuer, loggers, who, http, ct));
    }

    private static object Describe(IssuedLicence l) => new
    {
        l.Id,
        l.LicenceId,
        l.HospitalName,
        l.IssuedOn,
        l.ExpiresOn,
        l.DurationDays,
        l.MaxEquipment,
        modules = l.Modules.Length == 0 ? [] : l.Modules.Split(','),
        l.Notes,
        l.IssuedAtUtc,
        l.IsLocked,
        l.LockSequence,
        l.LockChangedAtUtc,
    };

    private static async Task<IResult> ListAsync(HospitalPmDbContext db, LicenceIssuer issuer, CancellationToken ct)
    {
        var (available, problem) = issuer.Availability();
        var rows = await db.IssuedLicences.AsNoTracking()
            .OrderByDescending(l => l.IssuedAtUtc)
            .ToListAsync(ct);

        return Results.Ok(new { available, problem, licences = rows.Select(Describe) });
    }

    private static async Task<IResult> IssueAsync(
        IssueLicenceRequest request,
        LicenceIssuer issuer,
        System.Security.Claims.ClaimsPrincipal who,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (issuer.Availability() is (false, var problem))
        {
            return Results.Conflict(new { error = problem });
        }

        try
        {
            var row = await issuer.IssueAsync(request, PermissionService.UserIdOf(who) ?? 0, ct);
            loggers.CreateLogger("HospitalPm.Licences").LogWarning(
                "{User} issued licence {Licence} to {Hospital}", who.Identity?.Name ?? "someone", row.LicenceId, row.HospitalName);

            return Results.Created($"/api/developer/licences/{row.Id}", new { licence = Describe(row), licenceText = row.LicenceText });
        }
        catch (IssueException e)
        {
            return Results.BadRequest(new { error = e.Message });
        }
    }

    /// <summary>The licence as it was sent, to send again. The same signed text: nothing is signed a second time.</summary>
    private static async Task<IResult> FileAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        var row = await db.IssuedLicences.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, ct);
        if (row is null)
        {
            return Results.NotFound(new { error = "No licence with that id." });
        }

        var slug = new string(row.HospitalName.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return Results.File(System.Text.Encoding.UTF8.GetBytes(row.LicenceText), "text/plain; charset=utf-8", $"hospitalpm-{(slug.Length == 0 ? "licence" : slug)}.licence");
    }

    private static async Task<IResult> CommandAsync(
        int id,
        string action,
        LicenceIssuer issuer,
        ILoggerFactory loggers,
        System.Security.Claims.ClaimsPrincipal who,
        HttpContext http,
        CancellationToken ct)
    {
        if (issuer.Availability() is (false, var problem))
        {
            return Results.Conflict(new { error = problem });
        }

        try
        {
            var (code, row) = await issuer.CommandAsync(id, action, ct);
            loggers.CreateLogger("HospitalPm.Licences").LogWarning(
                "{User} made {Action} code number {Sequence} for licence {Licence} ({Hospital})",
                who.Identity?.Name ?? "someone", action, row.LockSequence, row.LicenceId, row.HospitalName);

            // A code is something to be sent on: never kept by a browser or a proxy.
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { code, sequence = row.LockSequence, action, licence = Describe(row) });
        }
        catch (KeyNotFoundException e)
        {
            return Results.NotFound(new { error = e.Message });
        }
        catch (IssueException e)
        {
            return Results.BadRequest(new { error = e.Message });
        }
    }
}
