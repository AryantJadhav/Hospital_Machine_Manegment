using System.Diagnostics;
using System.Text;
using HospitalPm.Domain.Operations;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HospitalPm.Infrastructure.Operations;

/// <summary>
/// Takes a backup of the database with pg_dump, reads it back, and records
/// what happened.
///
/// The read-back is the point. A dump that was written but cannot be restored
/// is the failure this whole feature exists to prevent, and it is invisible
/// until the day someone needs it. pg_restore --list parses the archive's
/// table of contents, which catches a truncated or corrupt file for the cost
/// of a second or two.
///
/// Runs as a Hangfire job in the same process, so a client install still has
/// two services and no extra daemon.
/// </summary>
public sealed partial class BackupService(
    HospitalPmDbContext db,
    PgToolLocator locator,
    IConfiguration configuration,
    IOptions<BackupOptions> options,
    TimeProvider clock,
    ILogger<BackupService> logger)
{
    private readonly BackupOptions _options = options.Value;

    /// <summary>Hangfire entry point. Never throws: a failed backup is a recorded row.</summary>
    public Task RunScheduledAsync(CancellationToken ct = default) =>
        RunAsync(BackupTrigger.Scheduled, ct);

    public async Task<BackupRun> RunAsync(BackupTrigger trigger, CancellationToken ct = default)
    {
        var startedAt = clock.GetUtcNow().UtcDateTime;
        var stopwatch = Stopwatch.StartNew();

        var run = new BackupRun
        {
            StartedAtUtc = startedAt,
            Status = BackupStatus.Running,
            Trigger = trigger,
        };

        db.BackupRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            await ExecuteAsync(run, ct);
            run.Status = BackupStatus.Succeeded;
        }
        catch (Exception e)
        {
            // Deliberately broad. Anything that stops a backup — a full disk,
            // a missing tool, a killed child process — has to end as a row
            // saying so rather than an unobserved background exception.
            Log.Failed(logger, e);
            run.Status = BackupStatus.Failed;
            run.Error = Describe(e);
        }
        finally
        {
            stopwatch.Stop();
            run.FinishedAtUtc = clock.GetUtcNow().UtcDateTime;
            run.DurationMs = (int)stopwatch.ElapsedMilliseconds;
            await db.SaveChangesAsync(CancellationToken.None);
        }

        return run;
    }

    private async Task ExecuteAsync(BackupRun run, CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("HospitalPm");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("No database connection is configured.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var serverVersion = await ReadServerVersionAsync(connectionString, ct);
        run.ServerVersion = serverVersion?.ToString();

        var pgDump = locator.FindPgDump(serverVersion);
        if (!pgDump.IsUsable)
        {
            throw new InvalidOperationException(pgDump.Problem ?? "pg_dump is unavailable.");
        }

        var directory = ResolveDirectory();
        Directory.CreateDirectory(directory);

        // Sortable, unambiguous, and safe on both filesystems. Local time
        // would reorder itself twice a year.
        var fileName = $"hospitalpm-{run.StartedAtUtc:yyyyMMdd-HHmmss}.dump";
        var fullPath = Path.Combine(directory, fileName);

        // Custom format: compressed, and pg_restore can pull single tables out
        // of it. Plain SQL would be larger and all-or-nothing to restore.
        var arguments = new[]
        {
            "--format=custom",
            "--no-owner",
            "--no-privileges",
            $"--host={builder.Host}",
            $"--port={builder.Port}",
            $"--username={builder.Username}",
            $"--dbname={builder.Database}",
            $"--file={fullPath}",
        };

        await RunToolAsync(pgDump.Path!, arguments, builder.Password, "pg_dump", ct);

        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length == 0)
        {
            throw new InvalidOperationException(
                "pg_dump reported success but wrote no data. Check free disk space on the backup drive.");
        }

        run.FileName = fileName;
        run.SizeBytes = info.Length;

        if (_options.VerifyAfterWrite)
        {
            await VerifyAsync(fullPath, serverVersion, ct);
        }

        Prune(directory);
    }

    /// <summary>
    /// Reads the finished archive back. A backup nobody can open is not a
    /// backup, and this is the cheapest moment to find that out.
    /// </summary>
    private async Task VerifyAsync(string fullPath, Version? serverVersion, CancellationToken ct)
    {
        var pgRestore = locator.FindPgRestore(serverVersion);

        if (!pgRestore.IsUsable)
        {
            // The dump itself is written and its size is known. Refusing to
            // record it because the checker is missing would throw away a good
            // backup, so this degrades to a warning rather than a failure.
            Log.NotVerified(logger, pgRestore.Problem);
            return;
        }

        await RunToolAsync(pgRestore.Path!, ["--list", fullPath], password: null, "pg_restore", ct);
    }

    private async Task RunToolAsync(
        string exePath, string[] arguments, string? password, string toolName, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Through the environment, never the command line. Arguments are
        // visible to every user on the machine in the process list; a hospital
        // PC is a shared machine.
        if (!string.IsNullOrEmpty(password))
        {
            startInfo.Environment["PGPASSWORD"] = password;
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"{toolName} could not be started.");

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderr.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();

        // Read stdout too, so a chatty tool cannot fill the pipe buffer and
        // deadlock waiting for someone to drain it.
        _ = process.StandardOutput.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(_options.TimeoutMinutes));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new InvalidOperationException(
                $"{toolName} did not finish within {_options.TimeoutMinutes} minutes and was stopped.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var detail = stderr.ToString().Trim();
            throw new InvalidOperationException(
                detail.Length > 0
                    ? $"{toolName} failed: {Shorten(detail)}"
                    : $"{toolName} failed with exit code {process.ExitCode}.");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            // Already gone, or not ours to kill.
        }
    }

    /// <summary>
    /// Deletes the oldest dumps beyond the retention count.
    ///
    /// Only files this app named are considered. A hospital that points the
    /// backup directory at a shared drive must not have its other files
    /// deleted by us.
    /// </summary>
    private void Prune(string directory)
    {
        if (_options.RetainCount <= 0) return;

        try
        {
            var ours = new DirectoryInfo(directory)
                .EnumerateFiles("hospitalpm-*.dump")
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(_options.RetainCount)
                .ToList();

            foreach (var file in ours)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.PruneFileFailed(logger, file.Name, e);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Retention failing must never fail the backup that just succeeded.
            Log.PruneFailed(logger, directory, e);
        }
    }

    public string ResolveDirectory() =>
        Path.IsPathRooted(_options.Directory)
            ? _options.Directory
            : Path.Combine(AppContext.BaseDirectory, _options.Directory);

    private static async Task<Version?> ReadServerVersionAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            return connection.PostgreSqlVersion;
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns an exception into something a hospital IT contact can act on.
    /// The log keeps the stack trace; the dashboard gets a sentence.
    /// </summary>
    private static string Describe(Exception e) => e switch
    {
        InvalidOperationException => Shorten(e.Message),
        UnauthorizedAccessException => "Access denied writing to the backup directory. "
                                       + "Check the service account's permissions.",
        IOException io when io.Message.Contains("space", StringComparison.OrdinalIgnoreCase)
            => "The backup drive is out of space.",
        IOException => $"Could not write the backup file: {Shorten(e.Message)}",
        OperationCanceledException => "The backup was cancelled, most likely by a service restart.",
        _ => Shorten(e.Message),
    };

    private static string Shorten(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 500 ? collapsed : collapsed[..500] + "…";
    }

    /// <summary>
    /// Source-generated log messages. Backups run unattended, so the log is
    /// the only account of what happened on the nights nobody looked.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Backup failed")]
        public static partial void Failed(ILogger logger, Exception e);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Backup written but not verified: {Problem}")]
        public static partial void NotVerified(ILogger logger, string? problem);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete old backup {File}")]
        public static partial void PruneFileFailed(ILogger logger, string file, Exception e);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Could not prune old backups in {Directory}")]
        public static partial void PruneFailed(ILogger logger, string directory, Exception e);
    }
}
