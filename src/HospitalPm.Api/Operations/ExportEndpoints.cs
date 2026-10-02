using HospitalPm.Api.Auth;
using System.Security.Claims;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Export;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using HospitalPm.Infrastructure.Reports;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Operations;

/// <summary>
/// The whole register as files the hospital keeps: administrators only, because
/// it is all of the hospital's records at once.
///
/// A read, so it keeps working when the licence has turned the software
/// read-only. Taking your data out must never be the thing a lapsed payment
/// prevents.
/// </summary>
public static class ExportEndpoints
{
    public static void MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/export", ExportAsync)
            .WithTags("Export")
            .RequirePermission(Permissions.DataExport);
    }

    private static async Task ExportAsync(
        HttpContext http,
        DataExportService export,
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal user,
        [FromQuery] bool? signatures,
        CancellationToken ct)
    {
        // The zip library writes synchronously. Allowed for this one response,
        // which is streamed out a batch at a time, and nowhere else.
        http.Features.Get<IHttpBodyControlFeature>()!.AllowSynchronousIO = true;

        var sub = user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                  ?? user.FindFirstValue(ClaimTypes.NameIdentifier);
        var name = int.TryParse(sub, out var id)
            ? await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.FullName).FirstOrDefaultAsync(ct)
            : null;

        var stamp = (clock.UtcNow() + clock.Offset).ToString("yyyyMMdd-HHmm");

        http.Response.ContentType = "application/zip";
        http.Response.Headers.ContentDisposition =
            $"attachment; filename=\"hospitalpm-export-{stamp}-{ReportTime.Zone(clock.Offset)}.zip\"";

        await export.WriteAsync(
            http.Response.Body,
            name ?? user.Identity?.Name ?? "Unknown",
            new ExportOptions(IncludeSignatures: signatures ?? false),
            ct);
    }
}
