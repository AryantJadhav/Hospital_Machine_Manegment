using System.Diagnostics;
using HospitalPm.Api.Hosting;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Operations;
using HospitalPm.Infrastructure.Operations;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Operations;

/// <summary>Typed by hand, so a restore cannot happen by clicking through a dialog.</summary>
public sealed record RestoreRequest(string Confirm);

/// <summary>
/// Restoring the database from one of its own backups.
///
/// The work is done by restore-database.ps1, launched and left to run. It has
/// to be: restoring means dropping and recreating the database, which cannot
/// be done by a process connected to it, and the first thing that script does
/// is stop this service. So the response goes out first and the caller watches
/// /health for the service coming back.
/// </summary>
public static class RestoreEndpoints
{
    private const string ConfirmationWord = "RESTORE";

    public static void MapRestoreEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/admin/backups/{id:int}/restore", RestoreAsync)
            .WithTags("Backups")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }

    private static async Task<IResult> RestoreAsync(
        int id,
        RestoreRequest request,
        HospitalPmDbContext db,
        BackupService backups,
        ILogger<Program> logger,
        CancellationToken ct)
    {
        if (!string.Equals(request.Confirm, ConfirmationWord, StringComparison.Ordinal))
        {
            return Results.BadRequest(new
            {
                error = $"Type {ConfirmationWord} to confirm. This replaces the current database.",
            });
        }

        var run = await db.BackupRuns.AsNoTracking()
            .Where(r => r.Id == id && r.Status == BackupStatus.Succeeded && r.FileName != null)
            .Select(r => new { r.FileName })
            .SingleOrDefaultAsync(ct);

        if (run?.FileName is null)
        {
            return Results.NotFound(new { error = "No successful backup with that id." });
        }

        var directory = backups.ResolveDirectory();
        var dumpPath = Path.Combine(directory, run.FileName);

        if (!File.Exists(dumpPath))
        {
            return Results.BadRequest(new
            {
                error = $"The backup file {run.FileName} is no longer in {directory}. "
                        + "It may have been removed by retention or moved off this machine.",
            });
        }

        var script = Path.Combine(AppContext.BaseDirectory, "restore-database.ps1");
        var pgRoot = Path.Combine(AppContext.BaseDirectory, "pgsql");

        // Only an installed system has these. A development run has no service
        // to stop and no bundled PostgreSQL, and should say so rather than
        // half-attempt it.
        if (!File.Exists(script) || !Directory.Exists(pgRoot))
        {
            return Results.BadRequest(new
            {
                error = "Restore is only available on an installed system. "
                        + "This looks like a development run.",
            });
        }

        var dataDirectory = InstallPaths.DataDirectory();
        var credentials = Path.Combine(dataDirectory, "db.json");

        if (!File.Exists(credentials))
        {
            return Results.BadRequest(new
            {
                error = $"The database credentials at {credentials} are missing, "
                        + "so a restore cannot sign in to PostgreSQL.",
            });
        }

        var log = Path.Combine(dataDirectory, "restore.log");

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in new[]
                 {
                     "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script,
                     "-PgRoot", pgRoot,
                     "-CredentialsFile", credentials,
                     "-DumpFile", dumpPath,
                     "-BackupDir", directory,
                     "-LogFile", log,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            // Started and deliberately not awaited. This service is about to be
            // stopped by the very process being launched.
            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return Results.Problem("The restore could not be started.");
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StartupLog.RestoreLaunchFailed(logger, e);
            return Results.Problem("The restore could not be started. See the Windows Event Log.");
        }

        // 202: it has been accepted and is running, and this service will stop
        // within seconds. Anything more definite would be a lie.
        return Results.Accepted(value: new
        {
            message = "The restore has started. Hospital PM will stop for about a minute "
                      + "and come back with the restored data. Refresh this page then.",
            fileName = run.FileName,
            logFile = log,
        });
    }
}
