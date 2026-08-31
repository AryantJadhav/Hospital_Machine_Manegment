using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Operations;
using HospitalPm.Infrastructure.Operations;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HospitalPm.Api.Operations;

/// <summary>
/// Backup status and on-demand backups.
///
/// Admin only. The list says where dumps are written and whether pg_dump can
/// actually be found, because the two questions a hospital's IT contact asks
/// are "is it backing up" and "where is the file" — and both need answering
/// before the day they matter.
/// </summary>
public static class BackupEndpoints
{
    private const int RecentRuns = 30;

    public static void MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/backups")
            .WithTags("Backups")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        group.MapGet("/", StatusAsync);
        group.MapPost("/run", RunAsync);
    }

    private static async Task<IResult> StatusAsync(
        HospitalPmDbContext db,
        BackupService service,
        PgToolLocator locator,
        IOptions<BackupOptions> options,
        CancellationToken ct)
    {
        var runs = await db.BackupRuns.AsNoTracking()
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(RecentRuns)
            .Select(r => new
            {
                r.Id,
                r.StartedAtUtc,
                r.FinishedAtUtc,
                r.Status,
                r.Trigger,
                r.FileName,
                r.SizeBytes,
                r.Error,
                r.DurationMs,
            })
            .ToListAsync(ct);

        // Reported even when every recent run succeeded: a machine whose
        // PostgreSQL was upgraded under it still shows green here until the
        // next nightly run fails.
        var pgDump = locator.FindPgDump(serverVersion: null);

        return Results.Ok(new
        {
            runs,
            directory = service.ResolveDirectory(),
            retainCount = options.Value.RetainCount,
            tool = new
            {
                found = pgDump.Path is not null,
                path = pgDump.Path,
                version = pgDump.Version?.ToString(),
                problem = pgDump.Problem,
            },
            lastSuccessAtUtc = await db.BackupRuns.AsNoTracking()
                .Where(r => r.Status == BackupStatus.Succeeded)
                .OrderByDescending(r => r.StartedAtUtc)
                .Select(r => (DateTime?)r.StartedAtUtc)
                .FirstOrDefaultAsync(ct),
        });
    }

    /// <summary>
    /// Runs a backup now and waits for it.
    ///
    /// Synchronous on purpose: an administrator clicking "Back up now" before
    /// a server move needs to know whether it worked, and a 202 with a job id
    /// would mean building a progress channel to answer the same question.
    /// A failed backup is still a 200 — the run happened, and its outcome is
    /// in the body.
    /// </summary>
    private static async Task<IResult> RunAsync(BackupService service, CancellationToken ct)
    {
        var run = await service.RunAsync(BackupTrigger.Manual, ct);

        return Results.Ok(new
        {
            run.Id,
            run.Status,
            run.FileName,
            run.SizeBytes,
            run.Error,
            run.DurationMs,
        });
    }
}
