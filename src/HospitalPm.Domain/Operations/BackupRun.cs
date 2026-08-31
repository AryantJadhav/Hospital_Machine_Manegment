namespace HospitalPm.Domain.Operations;

public enum BackupStatus
{
    /// <summary>Started and not yet finished. A row left here means the service died mid-dump.</summary>
    Running = 10,

    /// <summary>Dump written and its archive read back successfully.</summary>
    Succeeded = 20,

    Failed = 30,
}

public enum BackupTrigger
{
    Scheduled = 10,
    Manual = 20,
}

/// <summary>
/// One attempt at backing up the database.
///
/// Failures are recorded, not just successes. A hospital that believes it has
/// backups because nobody told it otherwise is worse off than one that knows
/// it has none — so a run that fails leaves a row saying why, and the
/// dashboard reads the most recent run whatever its outcome.
/// </summary>
public sealed class BackupRun
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public DateTime StartedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }

    public BackupStatus Status { get; set; } = BackupStatus.Running;

    public BackupTrigger Trigger { get; set; } = BackupTrigger.Scheduled;

    /// <summary>
    /// File name only, never the full path. The backup directory moves when a
    /// hospital repoints it at a NAS, and a stored absolute path would then
    /// describe somewhere the file no longer is.
    /// </summary>
    public string? FileName { get; set; }

    public long? SizeBytes { get; set; }

    /// <summary>
    /// Why it failed, in words an administrator can act on. Never a stack
    /// trace: the reader is a hospital IT contact, not an engineer.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Server version the dump was taken from, recorded because a restore
    /// years later needs to know what it is restoring into.
    /// </summary>
    public string? ServerVersion { get; set; }

    public int? DurationMs { get; set; }
}
