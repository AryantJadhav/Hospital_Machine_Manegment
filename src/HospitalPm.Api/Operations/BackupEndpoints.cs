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

        // Sending the encrypted backups to the drive: what has gone and what has not, and a button to send now.
        group.MapPost("/drive/sync", DriveSyncAsync);

        // Taking a backup away, and bringing one in. The file stays encrypted both ways.
        group.MapPost("/{id:int}/download-link", DownloadLinkAsync);
        group.MapPost("/upload", UploadAsync)
            .RequirePermission(Permissions.SystemRestore);

        // The link the browser follows to save the file: it cannot send the sign-in, so the pass in the link is
        // the sign-in, for one file, once (see DownloadTickets). Not under the group, which asks for a sign-in.
        app.MapGet("/api/admin/backups/download/{token}", DownloadAsync)
            .WithTags("Backups")
            .AllowAnonymous();

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
        DriveSync drive,
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
            drive = drive.Status(),
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
    /// A link to save one backup file. Only an encrypted file is handed out: a plain dump is the whole database
    /// in the clear, and the page does not let that leave the machine.
    /// </summary>
    private static async Task<IResult> DownloadLinkAsync(
        int id,
        HospitalPmDbContext db,
        BackupService service,
        DownloadTickets tickets,
        System.Security.Claims.ClaimsPrincipal principal,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var fileName = await db.BackupRuns.AsNoTracking()
            .Where(r => r.Id == id && r.Status == BackupStatus.Succeeded && r.FileName != null)
            .Select(r => r.FileName)
            .SingleOrDefaultAsync(ct);

        if (fileName is null)
        {
            return Results.NotFound(new { error = "No successful backup with that id." });
        }

        var path = Path.Combine(service.ResolveDirectory(), fileName);
        if (!File.Exists(path))
        {
            return Results.BadRequest(new
            {
                error = $"The backup file {fileName} is no longer on this machine. "
                        + "It may have been removed by retention or moved away.",
            });
        }

        if (!BackupVault.LooksEncrypted(path))
        {
            return Results.Conflict(new
            {
                error = "That backup is not encrypted, so it cannot be downloaded. "
                        + "Run a new backup with encryption on, and download that one.",
            });
        }

        loggers.CreateLogger("HospitalPm.Backups").LogWarning(
            "{User} asked to download backup {File}", principal.Identity?.Name ?? "someone", fileName);

        return Results.Ok(new { url = $"/api/admin/backups/download/{tickets.Issue(path, fileName)}", fileName });
    }

    private static IResult DownloadAsync(string token, DownloadTickets tickets, HttpContext http)
    {
        var ticket = tickets.Redeem(token);
        if (ticket is null || !File.Exists(ticket.Value.Path))
        {
            // The same answer for a pass never issued, one already used and one that ran out.
            return Results.NotFound(new { error = "This download link has expired. Go back to the Backups page and ask for a new one." });
        }

        http.Response.Headers.CacheControl = "no-store";
        return Results.File(ticket.Value.Path, "application/octet-stream", ticket.Value.DownloadName);
    }

    /// <summary>
    /// A backup file brought from somewhere else, to be restored. The body is the file itself, not a form, so a
    /// large one streams to disk and never sits in memory. The recovery key, when the file was made on another
    /// machine, travels in a header and is never written down.
    /// </summary>
    private static async Task<IResult> UploadAsync(
        HttpContext http,
        HttpRequest request,
        BackupService service,
        System.Security.Claims.ClaimsPrincipal principal,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        // A backup is as large as the database and its photos: the default 30 MB limit is for forms, not for this.
        // Not every server lets it be changed once reading starts, and the test server does not offer it.
        var limit = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = null;
        }

        var recoveryKey = request.Headers["X-Recovery-Key"].ToString();
        var name = Uri.UnescapeDataString(request.Headers["X-File-Name"].ToString());

        try
        {
            var run = await service.AdoptAsync(request.Body, name, string.IsNullOrWhiteSpace(recoveryKey) ? null : recoveryKey, ct);

            loggers.CreateLogger("HospitalPm.Backups").LogWarning(
                "{User} brought in backup {File} ({Size} bytes)", principal.Identity?.Name ?? "someone", run.FileName, run.SizeBytes);

            return Results.Ok(new { run.Id, run.FileName, run.SizeBytes });
        }
        catch (BackupUploadException e)
        {
            return Results.BadRequest(new { error = e.Message });
        }
        catch (BackupDecryptionException e)
        {
            return Results.BadRequest(new { error = e.Message, needsRecoveryKey = e.NeedsRecoveryKey });
        }
    }

    /// <summary>Sends what has not gone yet and says what happened. Waits, so the person pressing it sees the result.</summary>
    private static async Task<IResult> DriveSyncAsync(DriveSync drive, CancellationToken ct)
    {
        var result = await drive.SyncAsync(ct);
        return Results.Ok(new { result.Ran, result.Sent, result.Error, result.Skipped, status = drive.Status() });
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
