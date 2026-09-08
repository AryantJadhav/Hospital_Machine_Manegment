using System.Reflection;
using HospitalPm.Domain.Operations;
using HospitalPm.Domain.Updates;
using HospitalPm.Infrastructure.Operations;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HospitalPm.Infrastructure.Updates;

/// <summary>
/// Installing an update from a file, with no internet.
///
/// This is the air-gapped half of updating, and the half that has to work
/// first: a biomedical department's PC frequently has no route out, and the
/// realistic delivery is someone walking in with a USB stick. Nothing here
/// makes a network call.
///
/// The order of operations is the whole design and is not negotiable:
///
///   1. Verify the signature. Before reading the installer, before hashing
///      it, before touching the database.
///   2. Copy the installer somewhere local, then hash the copy. The stick can
///      be pulled out mid-install, and a file checked in place can be swapped
///      between the check and the launch.
///   3. Back up. Migrations are forward-only and there is no down-migration,
///      so a failed update's only route back is a restore. If the backup
///      fails, the update does not happen.
///   4. Hand over to the installer and stop existing.
///
/// Step 4 is why the app cannot update itself directly: Windows locks a
/// running executable, so the process being replaced cannot be the one doing
/// the replacing. The installer already knows how to stop the service, swap
/// the files and start it again, and migrations run on startup.
/// </summary>
public sealed partial class UpdateService(
    IOptions<UpdateOptions> options,
    IUpdateFileSystem files,
    IUpdateLauncher launcher,
    IUpdateDownloader downloader,
    BackupService backups,
    ILogger<UpdateService> logger)
{
    private readonly UpdateOptions _options = options.Value;

    /// <summary>A manifest is a few hundred bytes. Anything near this is not one.</summary>
    private const int MaxManifestBytes = 64 * 1024;

    /// <summary>
    /// Set once, for the life of the process, the moment an installer is
    /// handed control.
    ///
    /// Two Inno installers racing to replace the same files is about the
    /// worst outcome this feature has, and it takes one impatient double-
    /// click: the first handover does not stop this service instantly, so a
    /// second request can arrive in the seconds before it dies. Static
    /// because there is one installation per process and the service is
    /// scoped per request.
    /// </summary>
    private static int _handedOver;

    /// <summary>The version this process is.</summary>
    public static Version RunningVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// Whether this build can install updates at all. A build with no public
    /// key cannot check a signature, and a build that cannot check a
    /// signature must not run a downloaded executable as LocalSystem.
    /// </summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(_options.PublicKey);

    public string DefaultFolder => Resolve(_options.Directory);

    /// <summary>
    /// Everything that looks like an update file in a folder, with a verdict
    /// on each. Unreadable and unsigned ones are returned rather than hidden:
    /// someone who copied the wrong file needs to be told that is what
    /// happened, not shown an empty list.
    /// </summary>
    public IReadOnlyList<UpdateCandidate> Scan(string? folder = null)
    {
        if (!Enabled) return [];

        var target = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder.Trim();

        string[] found;
        try
        {
            Directory.CreateDirectory(target);
            found = Directory.GetFiles(target, "*.update");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            // An unplugged drive letter or a folder nobody may read. Not an
            // error worth a 500 — the page says "nothing here" and the person
            // tries a different folder.
            Log.FolderUnreadable(logger, target, e);
            return [];
        }

        return [.. found
            .Select(path => VerifyOne(path, target))
            .OrderByDescending(c => c.Manifest?.Version, VersionText.Descending)];
    }

    /// <summary>
    /// Whether this install has been told where to look for updates online.
    /// Empty by default and correct for most hospitals, which have no route
    /// out at all.
    /// </summary>
    public bool CanCheckOnline =>
        Enabled
        && Uri.TryCreate(_options.FeedUrl, UriKind.Absolute, out var url)
        && url.Scheme == Uri.UriSchemeHttps;

    public string? FeedUrl => string.IsNullOrWhiteSpace(_options.FeedUrl) ? null : _options.FeedUrl.Trim();

    /// <summary>
    /// Fetches the newest update file and, if it is one we would install,
    /// fetches the installer it names — leaving both in the update folder for
    /// the ordinary install path to pick up.
    ///
    /// The download is not a second way to trust something. It is a second way
    /// to get the same two files onto the same disk, and what happens to them
    /// afterwards is identical to a USB stick: same signature, same hash, same
    /// backup, same handover. Nothing here decides anything.
    ///
    /// Deliberately not automatic and not scheduled. This runs when an
    /// administrator presses a button, so a hospital's network sees one
    /// outbound request that somebody asked for, rather than a service that
    /// phones out on its own.
    /// </summary>
    public async Task<OnlineCheck> CheckOnlineAsync(bool downloadInstaller, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return OnlineCheck.Unavailable(
                "This build cannot verify an update's signature, so it will not download one.");
        }

        if (!CanCheckOnline)
        {
            return OnlineCheck.Unavailable(
                "No update address is configured for this installation, so there is nothing to check. "
                + "Updates can still be copied across on a USB stick.");
        }

        var feed = new Uri(_options.FeedUrl.Trim());

        var manifest = await downloader.GetTextAsync(feed, MaxManifestBytes, ct);
        if (!manifest.Ok) return OnlineCheck.Unavailable(manifest.Problem!);

        Directory.CreateDirectory(DefaultFolder);

        // Written to disk before verification and re-read from there, so the
        // thing that gets checked is the thing that will be installed. The
        // alternative — verify in memory, write afterwards — leaves the file
        // that was signed and the file on disk as two separate claims.
        var manifestPath = Path.Combine(DefaultFolder, DownloadedManifestName);
        await File.WriteAllTextAsync(manifestPath, manifest.Text!, ct);

        var candidate = Verify(manifestPath);

        // The installer is not there yet, so InstallerMissing is the expected
        // verdict at this point rather than a problem. Everything else is a
        // real answer and stops here.
        if (candidate.State is not (UpdateState.Ready or UpdateState.InstallerMissing))
        {
            if (candidate.State is UpdateState.NotOurs or UpdateState.Unreadable)
            {
                // Nothing signed by us: do not leave it lying in the folder
                // looking like a pending update.
                TryDelete(manifestPath);
            }
            return new OnlineCheck(true, candidate, null);
        }

        if (!downloadInstaller)
        {
            return new OnlineCheck(true, candidate, null);
        }

        var installerName = candidate.Manifest!.InstallerFileName;

        // Built from the feed's own folder, and the file name has already been
        // forced to be a bare name — no slashes, no colon, no traversal — so a
        // signed manifest cannot point this at another host or another path.
        var installerUrl = new Uri(feed, installerName);
        var installerPath = Path.Combine(DefaultFolder, installerName);

        var download = await downloader.GetFileAsync(
            installerUrl, installerPath, _options.MaxInstallerBytes, ct);

        if (!download.Ok)
        {
            return new OnlineCheck(true, candidate, download.Problem);
        }

        // Verified again now the installer is present. This is the hash check,
        // and a download that fails it is deleted rather than left for someone
        // to wonder about.
        var verified = Verify(manifestPath);
        if (!verified.CanInstall)
        {
            TryDelete(installerPath);
        }

        return new OnlineCheck(true, verified, null);
    }

    /// <summary>
    /// One fixed name, so a check that runs twice replaces the previous
    /// manifest instead of littering the folder with near-identical files.
    /// </summary>
    private const string DownloadedManifestName = "downloaded.update";

    /// <summary>Verifies one named update file, re-reading it from disk.</summary>
    public UpdateCandidate Verify(string manifestPath)
    {
        if (!Enabled)
        {
            return new UpdateCandidate(UpdateState.NotOurs, null, manifestPath, null,
                "This build cannot verify updates, so it will not install one.");
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        return VerifyOne(manifestPath, folder ?? DefaultFolder);
    }

    private UpdateCandidate VerifyOne(string manifestPath, string folder)
    {
        string? text = null;
        try
        {
            text = File.ReadAllText(manifestPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.ManifestUnreadable(logger, manifestPath, e);
        }

        return new UpdateVerifier(_options.PublicKey)
            .Verify(manifestPath, text, folder, RunningVersion, files);
    }

    /// <summary>
    /// Verifies, stages, backs up, and hands over.
    ///
    /// Returns only when the installer has been started — which is the last
    /// thing this process does before the installer stops it. Everything that
    /// can be checked is checked before that point, because after it there is
    /// nobody left to report to.
    /// </summary>
    public async Task<UpdateHandover> InstallAsync(string manifestPath, CancellationToken ct = default)
    {
        // Re-verified from disk rather than trusting whatever the page was
        // showing. The scan may be minutes old and the file may have been
        // swapped since.
        var candidate = Verify(manifestPath);
        if (!candidate.CanInstall)
        {
            return UpdateHandover.Refused(candidate.Message);
        }

        var manifest = candidate.Manifest!;
        var source = candidate.InstallerPath!;

        string staged;
        try
        {
            staged = Stage(source, manifest);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.StagingFailed(logger, source, e);
            return UpdateHandover.Refused(
                "The installer could not be copied to this machine. Check there is enough free disk space, "
                + "then try again.");
        }

        // The copy is hashed, not the original. This is the whole reason for
        // staging: what runs must be what was verified, and a file on a USB
        // stick verified in place is not the same claim.
        if (!string.Equals(files.Sha256Hex(staged), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(staged);
            return UpdateHandover.Refused(
                "The installer changed while it was being copied. Nothing has been installed. "
                + "Copy the files across again and retry.");
        }

        if (_options.BackupFirst)
        {
            BackupRun run;
            try
            {
                run = await backups.RunAsync(BackupTrigger.Manual, ct);
            }
            catch (Exception e)
            {
                Log.BackupThrew(logger, e);
                return UpdateHandover.Refused(
                    "The backup before updating failed, so nothing has been installed. "
                    + "Fix the backup on the Backups page first — an update cannot be undone without one.");
            }

            if (run.Status != BackupStatus.Succeeded)
            {
                return UpdateHandover.Refused(
                    "The backup before updating did not succeed, so nothing has been installed. "
                    + $"{run.Error ?? "See the Backups page."} An update cannot be undone without a backup.");
            }

            Log.BackupTaken(logger, run.Id);
        }

        // Claimed before the launch, not after: the window this closes is
        // measured in seconds and the launch is the thing being guarded.
        if (Interlocked.Exchange(ref _handedOver, 1) == 1)
        {
            TryDelete(staged);
            return UpdateHandover.Refused(
                "An update is already being installed on this machine. Wait for the service to "
                + "restart rather than starting a second one.");
        }

        var logPath = Path.Combine(
            Resolve(_options.StagingDirectory),
            $"install-{manifest.Version}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log");

        Log.HandingOver(logger, manifest.Version);

        launcher.Launch(staged, logPath);

        return UpdateHandover.HandedOver(manifest.Version, logPath);
    }

    /// <summary>
    /// Copies the installer next to where its log will be written. Any
    /// previous staged copy goes first — they are hundreds of megabytes each
    /// and a hospital PC's disk is not large.
    /// </summary>
    private string Stage(string source, UpdateManifest manifest)
    {
        var staging = Resolve(_options.StagingDirectory);
        Directory.CreateDirectory(staging);

        foreach (var old in Directory.GetFiles(staging, "*.exe"))
        {
            TryDelete(old);
        }

        var destination = Path.Combine(staging, manifest.InstallerFileName);
        File.Copy(source, destination, overwrite: true);
        return destination;
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.DeleteFailed(logger, path, e);
        }
    }

    /// <summary>
    /// Same rule the backup directory follows: an absolute path is taken as
    /// given, and a relative one hangs off the binary. On a real install both
    /// are absolute and point under ProgramData, because the installer writes
    /// them there - Program Files is readable by every local user and is
    /// removed by an uninstall, and neither suits a staged installer.
    /// </summary>
    private static string Resolve(string configured) =>
        Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);

    /// <summary>
    /// Source-generated log messages. An update is the one operation whose
    /// own record does not survive it: the process that would explain a
    /// failure is the process being replaced. What is written here before
    /// the handover is all there will be.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Could not read the update folder {Folder}")]
        public static partial void FolderUnreadable(ILogger logger, string folder, Exception e);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Could not read the update file {Path}")]
        public static partial void ManifestUnreadable(ILogger logger, string path, Exception e);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Could not stage the installer from {Source}")]
        public static partial void StagingFailed(ILogger logger, string source, Exception e);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "The pre-update backup failed, so the update was not started")]
        public static partial void BackupThrew(ILogger logger, Exception e);

        [LoggerMessage(Level = LogLevel.Information,
            Message = "Pre-update backup {BackupId} succeeded")]
        public static partial void BackupTaken(ILogger logger, int backupId);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Updating to {Version}. This service is about to be stopped by the installer")]
        public static partial void HandingOver(ILogger logger, string version);

        [LoggerMessage(Level = LogLevel.Information, Message = "Could not delete {Path}")]
        public static partial void DeleteFailed(ILogger logger, string path, Exception e);
    }
}

/// <summary>What happened when an install was asked for.</summary>
/// <param name="Started">True once the installer is running and this service is on borrowed time.</param>
/// <param name="Version">The version being installed.</param>
/// <param name="LogPath">Where the installer will write its log, for when it goes wrong.</param>
/// <param name="Message">Why it was refused, when it was.</param>
/// <summary>What came back from looking online.</summary>
/// <param name="Reachable">False when there was nothing to ask, or nobody answered.</param>
/// <param name="Candidate">The verdict on what was fetched, when something was.</param>
/// <param name="Problem">Why the fetch fell short, in plain English.</param>
public sealed record OnlineCheck(bool Reachable, UpdateCandidate? Candidate, string? Problem)
{
    public static OnlineCheck Unavailable(string problem) => new(false, null, problem);
}

public sealed record UpdateHandover(bool Started, string? Version, string? LogPath, string? Message)
{
    public static UpdateHandover Refused(string message) => new(false, null, null, message);

    public static UpdateHandover HandedOver(string version, string logPath) =>
        new(true, version, logPath, null);
}

/// <summary>Newest first, with anything unparseable last.</summary>
internal sealed class VersionText : IComparer<string?>
{
    public static readonly IComparer<string?> Descending = new VersionText();

    public int Compare(string? x, string? y)
    {
        var left = Version.TryParse(x, out var a) ? a : null;
        var right = Version.TryParse(y, out var b) ? b : null;

        if (left is null && right is null) return 0;
        if (left is null) return 1;
        if (right is null) return -1;
        return right.CompareTo(left);
    }
}
