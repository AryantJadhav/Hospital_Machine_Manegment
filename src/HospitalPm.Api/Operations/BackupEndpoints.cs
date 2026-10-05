using HospitalPm.Api.Auth;
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
            .RequirePermission(Permissions.SystemBackups);

        group.MapGet("/", StatusAsync);
        group.MapPost("/run", RunAsync);

        // The keys that keep the backup files private. The recovery key is shown once, when it is made.
        group.MapPost("/encryption/recovery-key", NewRecoveryKeyAsync);
        group.MapPost("/encryption/recovery-key/saved", RecoveryKeySaved);
    }

    private static async Task<IResult> StatusAsync(
        HospitalPmDbContext db,
        BackupService service,
        BackupVault vault,
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
            // Whether backups are kept private, and whether the recovery key has been written down. Nothing here is a key.
            encryption = EncryptionStatus(vault, options.Value),
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

    private static object EncryptionStatus(BackupVault vault, BackupOptions options)
    {
        var keys = vault.Status();
        return new
        {
            enabled = options.Encrypt,
            keyPresent = keys.MasterKeyPresent,
            recoveryKeyCreatedAtUtc = keys.RecoveryKeyCreatedAtUtc,
            recoveryKeySaved = keys.RecoveryKeySaved,
        };
    }

    /// <summary>
    /// Makes a new recovery key and returns it, once. Every backup this machine can open is re-wrapped to it, so the
    /// key written down now opens all of them; the old one stops working. It is not kept anywhere it can be read
    /// without the machine's own key, so if it is lost the answer is to make another, not to look it up.
    /// </summary>
    private static IResult NewRecoveryKeyAsync(
        BackupService service,
        BackupVault vault,
        System.Security.Claims.ClaimsPrincipal principal,
        ILoggerFactory loggers,
        HttpContext http)
    {
        var result = vault.CreateRecoveryKey(service.ResolveDirectory());

        loggers.CreateLogger("HospitalPm.Backups").LogWarning(
            "{User} created a new backup recovery key ({Rewrapped} backups re-wrapped, {Skipped} not openable by this machine)",
            principal.Identity?.Name ?? "someone", result.Rewrapped, result.Skipped);

        // A secret in a response: never kept by a browser or a proxy.
        http.Response.Headers.CacheControl = "no-store";

        return Results.Ok(new { recoveryKey = result.Key, result.Rewrapped, result.Skipped });
    }

    /// <summary>The administrator has written the recovery key down: the page stops asking.</summary>
    private static IResult RecoveryKeySaved(BackupVault vault)
    {
        vault.MarkRecoveryKeySaved();
        return Results.NoContent();
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
