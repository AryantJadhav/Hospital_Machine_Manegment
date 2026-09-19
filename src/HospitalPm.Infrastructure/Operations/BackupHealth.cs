using HospitalPm.Domain.Operations;

namespace HospitalPm.Infrastructure.Operations;

public enum BackupHealthState
{
    /// <summary>A good backup is recent and the newest attempt did not fail.</summary>
    Ok,

    /// <summary>
    /// Nothing has run yet, and the install is too new to have expected it.
    /// The first scheduled backup is overnight, so a hospital that installs in
    /// the afternoon has, correctly, no backup for several hours.
    /// </summary>
    Pending,

    /// <summary>A recent good backup exists, but the newest attempt failed.</summary>
    Warning,

    /// <summary>No good backup, or the last one is too old.</summary>
    Problem,
}

/// <summary>
/// One decision, shared by the dashboard banner, so it can be tested without a
/// database. The Diagnostics page reads the same limit.
/// </summary>
public static class BackupHealth
{
    public static BackupHealthState Evaluate(
        BackupStatus? newestAttempt,
        DateTime? lastSuccessUtc,
        DateTime? installedAtUtc,
        DateTime nowUtc)
    {
        var limit = TimeSpan.FromHours(DiagnosticsService.BackupStaleHours);

        if (lastSuccessUtc is null)
        {
            // A failure is a real problem however new the install is. Only the
            // absence of any attempt, or one still running, is expected early on.
            var young = installedAtUtc is not null && nowUtc - installedAtUtc.Value < limit;
            var failed = newestAttempt == BackupStatus.Failed;
            return young && !failed ? BackupHealthState.Pending : BackupHealthState.Problem;
        }

        if (nowUtc - lastSuccessUtc.Value > limit)
        {
            return BackupHealthState.Problem;
        }

        return newestAttempt == BackupStatus.Failed ? BackupHealthState.Warning : BackupHealthState.Ok;
    }
}
