using HospitalPm.Domain.Operations;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Hosting;

/// <summary>
/// Closes off backup runs that never finished.
///
/// A run is written as Running before pg_dump starts and updated when it
/// ends, so a row left in Running means the process went away in between.
/// That happens two ways, and both are normal:
///
///   - the service was stopped or the machine lost power mid-backup;
///   - a restore was performed, and the dump being restored had captured its
///     own backup_run row while that backup was still in flight.
///
/// The second is guaranteed: every restore inherits exactly one such row. Left
/// alone they sit on the Backups page as a backup that has been running for
/// weeks, which is alarming and wrong.
/// </summary>
public static class InterruptedBackups
{
    /// <summary>
    /// Takes the context directly rather than a WebApplication, so this can
    /// be exercised against a real database without booting a host.
    /// </summary>
    public static async Task<int> CloseAsync(
        HospitalPmDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var stranded = await db.BackupRuns
            .Where(r => r.Status == BackupStatus.Running)
            .ToListAsync(ct);

        if (stranded.Count == 0) return 0;

        foreach (var run in stranded)
        {
            run.Status = BackupStatus.Failed;
            run.FinishedAtUtc ??= run.StartedAtUtc;
            run.Error = "Interrupted. The service stopped while this backup was running, "
                        + "or the database was restored from a backup taken during it.";
        }

        await db.SaveChangesAsync(ct);
        StartupLog.ClosedInterruptedBackups(logger, stranded.Count);
        return stranded.Count;
    }
}
