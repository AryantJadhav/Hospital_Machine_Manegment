namespace HospitalPm.Infrastructure.Updates;

public sealed class UpdateOptions
{
    public const string Section = "Update";

    /// <summary>
    /// Our update signing key's public half, base64 SubjectPublicKeyInfo.
    /// Baked into the build by the release workflow, like the licence key.
    ///
    /// A different key from the licence one on purpose. This signature
    /// authorises an executable that runs as LocalSystem; a licence signature
    /// authorises a customer to use software they already have. Sharing one
    /// key would mean anyone trusted to issue licences is also trusted to
    /// push code to every hospital.
    ///
    /// Empty in a development build, which disables updating entirely rather
    /// than trusting anything.
    /// </summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Folder the update page looks in by default, relative to the data
    /// directory. Somewhere under ProgramData rather than Program Files, so
    /// it survives an uninstall and is not writable by every local user.
    /// </summary>
    public string Directory { get; set; } = "updates";

    /// <summary>
    /// Where a verified installer is copied before it is run, relative to the
    /// data directory.
    ///
    /// Running it straight off the USB stick would be two bugs at once: the
    /// stick can be pulled out halfway through, and the file could be swapped
    /// between the hash check and the launch. Copying first, then re-hashing
    /// the copy, closes both.
    /// </summary>
    public string StagingDirectory { get; set; } = "updates/staging";

    /// <summary>
    /// Whether to take a backup before handing over to the installer.
    ///
    /// Configurable but not meant to be turned off. Migrations are
    /// forward-only with no down-migration, so a failed update leaves a
    /// half-migrated database whose only route back is a restore. It exists
    /// as a setting so a hospital with its own backup regime can say so
    /// deliberately, not so anyone can skip it by accident.
    /// </summary>
    public bool BackupFirst { get; set; } = true;

    /// <summary>
    /// Where to fetch the newest update file from, if this hospital has a
    /// route out. Empty disables checking entirely, which is the default and
    /// is the correct setting for most installs.
    ///
    /// Points straight at a .update file, not at an index. A stable URL that
    /// always serves the newest one — GitHub's
    /// releases/latest/download/HospitalPM.update, or a hospital group's own
    /// mirror — means there is no second format to sign, parse and get wrong.
    /// The installer is fetched from the same folder, named by the manifest.
    ///
    /// HTTPS only. The signature is what makes a download safe to run, not the
    /// transport, but there is no reason to accept a plaintext channel when
    /// the alternative is a USB stick that already works.
    /// </summary>
    public string FeedUrl { get; set; } = string.Empty;

    /// <summary>
    /// A ceiling on what a download may write, because the far end is not
    /// trusted until its signature checks out and a hostile — or simply
    /// broken — server can otherwise fill a hospital's disk. The installer
    /// bundles PostgreSQL and is a couple of hundred megabytes; a gigabyte is
    /// generous.
    /// </summary>
    public long MaxInstallerBytes { get; set; } = 1_073_741_824;

    /// <summary>
    /// How long a download may take before it is abandoned. Generous, because
    /// a hospital's connection is often poor and this runs when someone asked
    /// it to, not on a schedule.
    /// </summary>
    public int DownloadTimeoutMinutes { get; set; } = 30;
}
