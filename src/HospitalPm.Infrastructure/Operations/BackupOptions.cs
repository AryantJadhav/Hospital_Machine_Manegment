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
    /// How many backups to keep. 168 is a fortnight at one every two hours, which fits comfortably on any disk a hospital
    /// PC has (a backup is a few megabytes) and covers a fortnight's holiday. Change it with <see cref="EveryHours"/>.
    /// </summary>
    public int RetainCount { get; set; } = 168;

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
    /// Whether new backups are encrypted. OFF by default (the owner's decision, 5 October 2026): backups are plain
    /// PostgreSQL dumps, so they can be opened and restored with ordinary tools and need no key. Switch it on and every
    /// backup is encrypted again (see BackupVault). Whatever this says, an encrypted backup already on the disk can still
    /// be read and restored: the code that opens them stays in the program.
    ///
    /// Said plainly: a plain backup holds the whole hospital's records and is as readable as the place it is kept. That
    /// includes a Google Drive it is sent to.
    /// </summary>
    public bool Encrypt { get; set; }

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

    /// <summary>
    /// How often a backup runs, in hours: 1, 2, 3, 4, 6, 8, 12 or 24. Every two hours by default, so at most two hours of
    /// work is ever lost. Anything else falls back to two rather than switching the backup off.
    /// </summary>
    public int EveryHours { get; set; } = DefaultEveryHours;

    public const int DefaultEveryHours = 2;

    /// <summary>The hours that divide a day, which are the only ones that give the same times every day.</summary>
    private static readonly int[] DayDivisors = [1, 2, 3, 4, 6, 8, 12, 24];

    /// <summary>The interval from <see cref="EveryHours"/>, or the default when it is not one that fits a day.</summary>
    public int EveryHoursChecked() => DayDivisors.Contains(EveryHours) ? EveryHours : DefaultEveryHours;

    /// <summary>
    /// The time of day the first backup runs, on the hospital's own clock, as "HH:mm"; the others follow every
    /// <see cref="EveryHours"/> hours. Midnight by default. For one backup a day, set <see cref="EveryHours"/> to 24 and this
    /// to the hour you want (03:00 is the quietest the PC ever is). A value that cannot be read falls back to midnight
    /// rather than switching the backup off.
    /// </summary>
    public string StartsAt { get; set; } = DefaultStartsAt;

    public const string DefaultStartsAt = "00:00";

    /// <summary>The time of day from <see cref="StartsAt"/>, or the default when it is not a time.</summary>
    public TimeSpan StartsAtLocal() =>
        TimeSpan.TryParseExact(StartsAt?.Trim(), @"h\:mm", System.Globalization.CultureInfo.InvariantCulture, out var time)
        && time >= TimeSpan.Zero && time < TimeSpan.FromHours(24)
            ? time
            : TimeSpan.Parse(DefaultStartsAt, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The cron line, in UTC, for a backup at <paramref name="start"/> and then every <paramref name="everyHours"/> hours on a clock
    /// <paramref name="offset"/> ahead of UTC. The scheduler works in UTC because the program carries no OS time zone data;
    /// the hospital's offset is applied here. Every day's times are the same, because the interval divides the day.
    /// </summary>
    public static string CronFor(TimeSpan start, TimeSpan offset, int everyHours)
    {
        var first = ((int)(start - offset).TotalMinutes % 1440 + 1440) % 1440;
        var minute = first % 60;
        var hours = Enumerable.Range(0, 24 / everyHours)
            .Select(k => (first / 60 + k * everyHours) % 24)
            .Distinct()
            .Order()
            .ToArray();

        return $"{minute} {string.Join(',', hours)} * * *";
    }

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

    /// <summary>
    /// For an ordinary (personal) Google account, which a service account cannot use because Google gives service
    /// accounts no storage in a personal Drive: the sign-in token that <c>rclone config</c> makes, as its JSON text.
    /// Used with the narrow <c>drive.file</c> scope, so the program can only ever see the files it created itself and
    /// never the rest of the account's Drive. When set, it is used instead of the service account.
    /// </summary>
    public string? TokenJson { get; set; }

    /// <summary>The same, as a file.</summary>
    public string? TokenFile { get; set; }

    /// <summary>
    /// Use a remote that rclone already knows, from an existing rclone.conf (the file <c>rclone config</c> writes,
    /// usually <c>%APPDATA%/rclone/rclone.conf</c>), instead of one built from the settings below. Quickest on a
    /// machine where rclone is already signed in to Drive. Needs <see cref="RemoteName"/>. rclone may write a renewed
    /// sign-in back to that file, which is what it is meant to do.
    /// </summary>
    public string? RcloneConfigFile { get; set; }

    /// <summary>The remote's name in that file, e.g. "gdrive".</summary>
    public string? RemoteName { get; set; }

    /// <summary>The Drive folder shared with the service account, in which every hospital's folder is made.</summary>
    public string? RootFolderId { get; set; }

    /// <summary>
    /// This installation's own folder under the root. Left empty it is the licence's id, so each hospital has one
    /// of its own and an installation with no licence has none.
    /// </summary>
    public string? Folder { get; set; }

    /// <summary>How many backups to keep on the drive. The oldest beyond this are removed from it. 168 is a fortnight at one every two hours.</summary>
    public int KeepCount { get; set; } = 168;

    /// <summary>How long one upload may run before it is stopped. The next night tries again.</summary>
    public int TimeoutMinutes { get; set; } = 120;

    /// <summary>
    /// Extra settings for the rclone remote, merged over the defaults (rclone's own option names, e.g. "type").
    /// For a drive other than Google's, or a plain folder or share: <c>Remote:type=local</c>.
    /// </summary>
    public Dictionary<string, string> Remote { get; set; } = [];
}
