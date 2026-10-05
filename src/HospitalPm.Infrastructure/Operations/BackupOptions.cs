namespace HospitalPm.Infrastructure.Operations;

/// <summary>
/// Backup configuration. Every value has a working default, because a
/// hospital install has nobody to fill in a config file.
/// </summary>
public sealed class BackupOptions
{
    public const string Section = "Backup";

    /// <summary>
    /// Where dumps are written. Relative paths resolve against the binary's
    /// own directory, not the working directory — the service is started by
    /// the Windows Service Control Manager, whose working directory is
    /// system32.
    /// </summary>
    public string Directory { get; set; } = "backups";

    /// <summary>
    /// How many daily backups to keep. Fourteen fits comfortably on any disk
    /// a hospital PC has and covers a fortnight's holiday.
    /// </summary>
    public int RetainCount { get; set; } = 14;

    /// <summary>
    /// Explicit path to pg_dump. Left empty, the app searches PATH and the
    /// usual install locations. The Phase 2 installer knows exactly where it
    /// put PostgreSQL and should write that path here.
    /// </summary>
    public string? PgDumpPath { get; set; }

    /// <summary>Explicit path to pg_restore, used only to read the archive back.</summary>
    public string? PgRestorePath { get; set; }

    /// <summary>
    /// How long a dump may run before it is killed. A stuck pg_dump holding
    /// a connection overnight is worse than a failed backup that says so.
    /// </summary>
    public int TimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// Whether backups are encrypted. On, and it should stay on: the file is the part that leaves the machine
    /// (a USB drive, a shared folder, a cloud account) and it holds the whole hospital's records. Off only to
    /// keep writing plain dumps, for a hospital that encrypts the backup drive itself and wants the old files.
    /// See BackupVault for how.
    /// </summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>
    /// Where the backup keys are kept. Left empty it is the "keys" folder of the data directory, beside the
    /// signing key. For the best protection it is NOT on the same drive as <see cref="Directory"/>: a key
    /// kept next to the backups protects only the copies that leave.
    /// </summary>
    public string? KeyDirectory { get; set; }

    /// <summary>
    /// Whether to read the finished archive back with pg_restore --list.
    /// On by default: an unreadable backup that nobody checked is the whole
    /// failure mode this feature exists to prevent.
    /// </summary>
    public bool VerifyAfterWrite { get; set; } = true;
}
