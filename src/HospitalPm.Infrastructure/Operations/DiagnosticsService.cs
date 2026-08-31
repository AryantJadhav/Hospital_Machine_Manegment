using System.Diagnostics;
using System.Reflection;
using HospitalPm.Domain.Operations;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HospitalPm.Infrastructure.Operations;

/// <summary>One thing that is either working or not, in words a non-engineer can read.</summary>
/// <param name="Name">What was checked.</param>
/// <param name="State">Ok, Warning or Problem.</param>
/// <param name="Detail">The reading itself — a size, a version, a count.</param>
/// <param name="Advice">What to do about it, when there is something to do.</param>
public sealed record Check(string Name, CheckState State, string Detail, string? Advice = null);

public enum CheckState
{
    Ok = 10,
    Warning = 20,
    Problem = 30,
}

public sealed record Diagnostics(
    CheckState Overall,
    IReadOnlyList<Check> Checks,
    string Version,
    TimeSpan Uptime,
    DateTime UtcNow,
    IReadOnlyList<string> Addresses);

/// <summary>
/// Answers "is this installation healthy" in one call.
///
/// The reader is a hospital's IT contact, or whoever picks up the phone when
/// something is wrong. Every check therefore reports a state, a plain reading,
/// and — where it matters — what to do about it. A page that says
/// "npgsql: connected" tells that person nothing.
///
/// /health stays a separate, unauthenticated liveness probe. This is the
/// diagnosis; that is the pulse.
/// </summary>
public sealed class DiagnosticsService(
    HospitalPmDbContext db,
    BackupService backups,
    PgToolLocator locator,
    IOptions<BackupOptions> backupOptions,
    TimeProvider clock)
{
    /// <summary>Below this, a backup or an import is about to start failing.</summary>
    private const long LowDiskBytes = 2L * 1024 * 1024 * 1024;

    private const long VeryLowDiskBytes = 512L * 1024 * 1024;

    /// <summary>A backup older than this is stale enough to act on.</summary>
    private const int BackupStaleHours = 48;

    private static readonly DateTime StartedAtUtc = DateTime.UtcNow;

    public async Task<Diagnostics> RunAsync(IReadOnlyList<string> addresses, CancellationToken ct = default)
    {
        var checks = new List<Check>
        {
            await DatabaseAsync(ct),
            await MigrationsAsync(ct),
            await BackupAsync(ct),
            BackupTool(),
            Disk(),
            Clock(),
        };

        return new Diagnostics(
            Overall: checks.Count == 0 ? CheckState.Ok : checks.Max(c => c.State),
            Checks: checks,
            Version: Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            Uptime: DateTime.UtcNow - StartedAtUtc,
            UtcNow: clock.GetUtcNow().UtcDateTime,
            Addresses: addresses);
    }

    private async Task<Check> DatabaseAsync(CancellationToken ct)
    {
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            var stopwatch = Stopwatch.StartNew();

            // Opened explicitly. PostgreSqlVersion is only populated on an open
            // connection, and EF closes the one it borrows as soon as a query
            // finishes — reading it afterwards throws, which this method's own
            // catch would have turned into "Cannot be reached" against a
            // perfectly healthy database.
            await db.Database.OpenConnectionAsync(ct);

            try
            {
                var version = connection.PostgreSqlVersion;

                var size = await db.Database
                    // Aliased "Value" because that is the column name
                    // EF Core's SqlQuery<T> shaper looks for on a scalar.
                    .SqlQuery<long>($"select pg_database_size(current_database()) as \"Value\"")
                    .SingleAsync(ct);

                stopwatch.Stop();

                return new Check(
                    "Database",
                    CheckState.Ok,
                    $"PostgreSQL {version}, {Bytes(size)}, responded in {stopwatch.ElapsedMilliseconds} ms");
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return new Check(
                "Database",
                CheckState.Problem,
                "Cannot be reached.",
                "Check that the PostgreSQL service is running on this machine.");
        }
    }

    /// <summary>
    /// Migrations run on start, so a pending one means the service failed to
    /// bring the schema up and is serving an older shape than the code expects.
    /// </summary>
    private async Task<Check> MigrationsAsync(CancellationToken ct)
    {
        try
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();

            return pending.Count == 0
                ? new Check("Schema", CheckState.Ok, "Up to date.")
                : new Check(
                    "Schema",
                    CheckState.Problem,
                    $"{pending.Count} migration(s) have not been applied: {string.Join(", ", pending.Take(3))}.",
                    "Restart the Hospital PM service. If it persists, the database user may not be allowed to change the schema.");
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
        {
            return new Check("Schema", CheckState.Problem, "Could not be read.",
                "The database is unreachable, so the schema version is unknown.");
        }
    }

    private async Task<Check> BackupAsync(CancellationToken ct)
    {
        try
        {
            var last = await db.BackupRuns.AsNoTracking()
                .OrderByDescending(r => r.StartedAtUtc)
                .Select(r => new { r.StartedAtUtc, r.Status, r.Error, r.SizeBytes })
                .FirstOrDefaultAsync(ct);

            if (last is null)
            {
                return new Check("Backups", CheckState.Problem, "No backup has ever run.",
                    "Open Backups and run one now.");
            }

            var lastSuccess = await db.BackupRuns.AsNoTracking()
                .Where(r => r.Status == BackupStatus.Succeeded)
                .OrderByDescending(r => r.StartedAtUtc)
                .Select(r => (DateTime?)r.StartedAtUtc)
                .FirstOrDefaultAsync(ct);

            if (lastSuccess is null)
            {
                return new Check("Backups", CheckState.Problem,
                    $"No backup has ever succeeded. The last attempt failed: {last.Error}",
                    "Open Backups and run one now to see the current error.");
            }

            var age = clock.GetUtcNow().UtcDateTime - lastSuccess.Value;
            var reading = $"Last good backup {Age(age)} ago"
                          + (last.SizeBytes is not null ? $", {Bytes(last.SizeBytes.Value)}." : ".");

            if (age.TotalHours > BackupStaleHours)
            {
                return new Check("Backups", CheckState.Problem, reading,
                    "Backups are not running. Check the most recent attempt on the Backups page.");
            }

            // A good backup within the window, but the newest attempt failed:
            // still worth a warning, because tonight's will probably fail too.
            return last.Status == BackupStatus.Failed
                ? new Check("Backups", CheckState.Warning,
                    reading + $" The most recent attempt failed: {last.Error}",
                    "The last backup is still usable, but the next one will likely fail too.")
                : new Check("Backups", CheckState.Ok, reading);
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
        {
            return new Check("Backups", CheckState.Problem, "Could not be read.",
                "The database is unreachable.");
        }
    }

    private Check BackupTool()
    {
        var tool = locator.FindPgDump(serverVersion: null);

        return tool.IsUsable
            ? new Check("Backup tool", CheckState.Ok, $"pg_dump {tool.Version} at {tool.Path}")
            : new Check("Backup tool", CheckState.Problem,
                tool.Problem ?? "pg_dump is unavailable.",
                "Backups cannot run until this is fixed.");
    }

    /// <summary>
    /// Free space where the backups go. That drive fills before anything else
    /// does, and it fills quietly.
    /// </summary>
    private Check Disk()
    {
        var directory = backups.ResolveDirectory();

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(directory));
            if (string.IsNullOrEmpty(root))
            {
                return new Check("Disk space", CheckState.Warning,
                    $"Could not determine the drive for {directory}.");
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return new Check("Disk space", CheckState.Problem,
                    $"The drive holding {directory} is not available.",
                    "If backups are on a network or removable drive, reconnect it.");
            }

            var free = drive.AvailableFreeSpace;
            var reading = $"{Bytes(free)} free on {drive.Name}, keeping "
                          + $"{backupOptions.Value.RetainCount} backups.";

            if (free < VeryLowDiskBytes)
            {
                return new Check("Disk space", CheckState.Problem, reading,
                    "Backups and imports will fail. Free space or move the backup folder.");
            }

            return free < LowDiskBytes
                ? new Check("Disk space", CheckState.Warning, reading,
                    "Getting low. Copy old backups off this machine and delete them.")
                : new Check("Disk space", CheckState.Ok, reading);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new Check("Disk space", CheckState.Warning,
                $"Could not be read for {directory}.");
        }
    }

    /// <summary>
    /// PM due dates are dates, not instants, so a machine whose clock has
    /// drifted marks work overdue on the wrong day. Worth showing rather than
    /// discovering through an argument about a late PM.
    /// </summary>
    private Check Clock()
    {
        var utc = clock.GetUtcNow().UtcDateTime;
        var local = TimeZoneInfo.Local;
        var offset = local.GetUtcOffset(utc);

        return new Check(
            "Clock",
            CheckState.Ok,
            $"{utc:yyyy-MM-dd HH:mm} UTC, machine time zone {local.Id} "
            + $"(UTC{(offset < TimeSpan.Zero ? "-" : "+")}{offset:hh\\:mm}).");
    }

    private static string Age(TimeSpan span) => span.TotalHours switch
    {
        < 1 => $"{Math.Max(1, (int)span.TotalMinutes)} min",
        < 48 => $"{(int)span.TotalHours} hours",
        _ => $"{(int)span.TotalDays} days",
    };

    private static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
    };
}
