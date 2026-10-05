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

    /// <summary>Sending the encrypted backups to a cloud drive, with rclone. Off unless switched on.</summary>
    public DriveOptions Drive { get; set; } = new();

    /// <summary>The backup folder as a full path. A relative one is beside the program, never the working directory.</summary>
    public string ResolveDirectory() =>
        Path.IsPathRooted(Directory) ? Directory : Path.Combine(AppContext.BaseDirectory, Directory);
}

/// <summary>
/// Copying the encrypted backups off the machine, to Google Drive, with rclone.
///
/// Off by default, and never needed: a hospital with no internet simply leaves it off. Only encrypted files are
/// ever sent, so what reaches the drive is useless without the keys. A failed upload never fails a backup.
/// </summary>
public sealed class DriveOptions
{
    public bool Enabled { get; set; }

    /// <summary>Where rclone is. Left empty: beside the program, then on the PATH.</summary>
    public string? RclonePath { get; set; }

    /// <summary>
    /// The Google service account's key (the JSON file's text). Set by the build, never in the repository. A copy
    /// of the program that carries it can reach whatever the account can: see docs/GOOGLE-DRIVE-BACKUP.md.
    /// </summary>
    public string? ServiceAccountJson { get; set; }

    /// <summary>The same, as a file, for a machine where an environment variable is awkward.</summary>
    public string? ServiceAccountFile { get; set; }

    /// <summary>The Drive folder shared with the service account, in which every hospital's folder is made.</summary>
    public string? RootFolderId { get; set; }

    /// <summary>
    /// This installation's own folder under the root. Left empty it is the licence's id, so each hospital has one
    /// of its own and an installation with no licence has none.
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>How many backups to keep on the drive. The oldest beyond this are removed from it.</summary>
    public int KeepCount { get; set; } = 14;

    /// <summary>How long one upload may run before it is stopped. The next night tries again.</summary>
    public int TimeoutMinutes { get; set; } = 120;

    /// <summary>
    /// Extra settings for the rclone remote, merged over the defaults (rclone's own option names, e.g. "type").
    /// For a drive other than Google's, or a plain folder or share: <c>Remote:type=local</c>.
    /// </summary>
    public Dictionary<string, string> Remote { get; set; } = [];
}
