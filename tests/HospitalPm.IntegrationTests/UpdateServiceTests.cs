using System.Security.Cryptography;
using HospitalPm.Domain.Updates;
using HospitalPm.Infrastructure.Operations;
using HospitalPm.Infrastructure.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The install flow, against real files and a real backup.
///
/// The verifier's rules are covered without a disk elsewhere. What is left
/// here is the part that only means something end to end: that the file which
/// actually gets executed is a local copy that was hashed after copying, that
/// a backup happened before the handover, and that a failed backup stops the
/// update rather than being logged and stepped over.
///
/// The launcher is the only thing faked, because the real one stops the
/// service this test host is running inside.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class UpdateServiceTests(PostgresFixture fixture) : IDisposable
{
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover temp directory is not worth failing a test run.
            }
        }
    }

    private string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "hospitalpm-update-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _directories.Add(path);
        return path;
    }

    /// <summary>Records what it was asked to run, and never runs it.</summary>
    private sealed class RecordingLauncher : IUpdateLauncher
    {
        public string? InstallerPath { get; private set; }
        public string? LogPath { get; private set; }
        public byte[]? ContentsAtLaunch { get; private set; }
        public int Calls { get; private set; }

        /// <summary>
        /// Sampled at the instant of handover, which is the only moment an
        /// ordering claim can honestly be checked. Asserting after the call
        /// returns would pass whether the backup ran before the launch or
        /// after it.
        /// </summary>
        public Func<int>? SampleAtLaunch { get; init; }

        public int? SampledAtLaunch { get; private set; }

        public void Launch(string installerPath, string logPath)
        {
            Calls++;
            InstallerPath = installerPath;
            LogPath = logPath;
            SampledAtLaunch = SampleAtLaunch?.Invoke();

            // Read at the moment of launch, so a test can assert what would
            // actually have been executed rather than what was on disk
            // earlier.
            ContentsAtLaunch = File.ReadAllBytes(installerPath);
        }
    }

    /// <summary>
    /// For the tests that have nothing to do with the network. Failing
    /// loudly rather than returning an empty answer, so a code path that
    /// starts reaching for the internet cannot do it quietly.
    /// </summary>
    private sealed class NeverCalledDownloader : IUpdateDownloader
    {
        public Task<DownloadOutcome> GetTextAsync(Uri url, int maxBytes, CancellationToken ct) =>
            throw new InvalidOperationException("This test should not have gone online.");

        public Task<DownloadOutcome> GetFileAsync(
            Uri url, string destination, long maxBytes, CancellationToken ct) =>
            throw new InvalidOperationException("This test should not have gone online.");
    }

    private sealed record Fixture(
        UpdateService Service,
        RecordingLauncher Launcher,
        string Stick,
        string ManifestPath,
        string InstallerPath,
        string StagingDirectory);

    /// <summary>
    /// A signed release sitting in a folder, exactly as it would arrive: two
    /// files side by side, signed with a key made here.
    /// </summary>
    private Fixture Arrange(
        string version = "9999.1.0",
        bool backupFirst = false,
        string? backupDirectory = null,
        Action<string>? corruptInstallerAfterSigning = null,
        Func<int>? sampleAtLaunch = null,
        IUpdateDownloader? downloader = null,
        string feedUrl = "")
    {
        // Each fixture is a fresh machine. The handover guard is a static
        // that production sets once and never clears - correct for a
        // service about to be replaced, and something a test host that
        // keeps running has to undo between cases.
        ResetHandoverGuard();

        var stick = NewDirectory();
        var staging = NewDirectory();

        var installerPath = Path.Combine(stick, $"HospitalPM-Setup-{version}.exe");
        var payloadBytes = RandomNumberGenerator.GetBytes(64 * 1024);
        File.WriteAllBytes(installerPath, payloadBytes);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

        var manifest = new UpdateManifest(
            Version: version,
            InstallerFileName: Path.GetFileName(installerPath),
            Sha256: Convert.ToHexStringLower(SHA256.HashData(payloadBytes)),
            SizeBytes: payloadBytes.Length,
            ReleasedOn: DateOnly.FromDateTime(DateTime.UtcNow),
            Notes: "Test release.");

        var manifestBytes = UpdateManifestFile.Serialise(manifest);
        var signature = key.SignData(
            manifestBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        var manifestPath = Path.Combine(stick, $"HospitalPM-{version}.update");
        File.WriteAllText(manifestPath, UpdateManifestFile.Format(manifestBytes, signature));

        // After signing, so the manifest describes the original file. This is
        // how a swap between issue and install looks.
        corruptInstallerAfterSigning?.Invoke(installerPath);

        var launcher = new RecordingLauncher { SampleAtLaunch = sampleAtLaunch };

        var backupOptions = new BackupOptions
        {
            Directory = backupDirectory ?? NewDirectory(),
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HospitalPm"] = fixture.ConnectionString,
            })
            .Build();

        var wrappedBackup = Options.Create(backupOptions);
        var backups = new BackupService(
            fixture.CreateContext(),
            new PgToolLocator(wrappedBackup),
            configuration,
            wrappedBackup,
            TimeProvider.System,
            new HospitalPm.Infrastructure.Maintenance.HospitalClock(TimeProvider.System, Options.Create(new HospitalPm.Infrastructure.Maintenance.ScheduleOptions())),
            NullLogger<BackupService>.Instance);

        var service = new UpdateService(
            Options.Create(new UpdateOptions
            {
                PublicKey = publicKey,
                Directory = stick,
                StagingDirectory = staging,
                BackupFirst = backupFirst,
                FeedUrl = feedUrl,
            }),
            new UpdateFileSystem(),
            launcher,
            downloader ?? new NeverCalledDownloader(),
            backups,
            new HospitalPm.Infrastructure.Maintenance.HospitalClock(TimeProvider.System, Options.Create(new HospitalPm.Infrastructure.Maintenance.ScheduleOptions())),
            NullLogger<UpdateService>.Instance);

        return new Fixture(service, launcher, stick, manifestPath, installerPath, staging);
    }

    [Fact]
    public void A_signed_release_in_the_folder_is_offered()
    {
        var f = Arrange();

        var found = f.Service.Scan();

        var one = Assert.Single(found);
        Assert.Equal(UpdateState.Ready, one.State);
        Assert.Equal("9999.1.0", one.Manifest?.Version);
    }

    /// <summary>
    /// A folder with nothing in it, a drive letter that is not plugged in, a
    /// path nobody may read: all the same answer, and none of them a 500.
    /// </summary>
    [Fact]
    public void An_unreadable_folder_is_empty_rather_than_an_error()
    {
        var f = Arrange();

        Assert.Empty(f.Service.Scan(Path.Combine(NewDirectory(), "nothing-here")));
    }

    [Fact]
    public async Task Installing_runs_a_local_copy_and_not_the_file_on_the_stick()
    {
        var f = Arrange();

        var result = await f.Service.InstallAsync(f.ManifestPath);

        Assert.True(result.Started, result.Message);
        Assert.Equal(1, f.Launcher.Calls);

        // The whole reason staging exists. Running the file in place would
        // mean a USB stick pulled out halfway through takes the install with
        // it, and would leave a window between the hash check and the launch
        // in which the file could be swapped.
        Assert.NotNull(f.Launcher.InstallerPath);
        Assert.StartsWith(f.StagingDirectory, f.Launcher.InstallerPath!, StringComparison.Ordinal);
        Assert.NotEqual(f.InstallerPath, f.Launcher.InstallerPath);

        // And what was staged is byte-for-byte what was signed.
        Assert.Equal(File.ReadAllBytes(f.InstallerPath), f.Launcher.ContentsAtLaunch);
    }

    [Fact]
    public async Task An_installer_swapped_after_signing_is_never_launched()
    {
        var f = Arrange(corruptInstallerAfterSigning: path =>
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(64 * 1024)));

        var result = await f.Service.InstallAsync(f.ManifestPath);

        Assert.False(result.Started);
        Assert.Equal(0, f.Launcher.Calls);
        Assert.Contains("not the file this update was issued for", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_installer_is_never_launched()
    {
        var f = Arrange();
        File.Delete(f.InstallerPath);

        var result = await f.Service.InstallAsync(f.ManifestPath);

        Assert.False(result.Started);
        Assert.Equal(0, f.Launcher.Calls);
    }

    [Fact]
    public async Task An_update_that_is_not_newer_is_never_launched()
    {
        // Below the test host's own version, which is what the service
        // compares against.
        var f = Arrange(version: "0.0.1");

        var result = await f.Service.InstallAsync(f.ManifestPath);

        Assert.False(result.Started);
        Assert.Equal(0, f.Launcher.Calls);
    }

    /// <summary>
    /// The claim that matters most. Migrations are forward-only and there is
    /// no down-migration, so an update that goes wrong has exactly one route
    /// back. If the backup did not happen, neither should the update.
    /// </summary>
    [Fact]
    public async Task A_backup_is_taken_before_the_installer_is_started()
    {
        var backupDirectory = NewDirectory();
        var f = Arrange(
            backupFirst: true,
            backupDirectory: backupDirectory,
            sampleAtLaunch: () => Directory.GetFiles(backupDirectory).Length);

        Assert.Empty(Directory.GetFiles(backupDirectory));
        var result = await f.Service.InstallAsync(f.ManifestPath);

        if (!ToolsUsable(backupDirectory))
        {
            // No usable pg_dump on this machine. The contract then is that
            // the update is refused, not that it proceeds unprotected —
            // which is itself worth asserting.
            Assert.False(result.Started);
            Assert.Equal(0, f.Launcher.Calls);
            Assert.Contains("backup", result.Message!, StringComparison.OrdinalIgnoreCase);
            return;
        }

        Assert.True(result.Started, result.Message);
        Assert.Equal(1, f.Launcher.Calls);

        // Not "a backup exists now" — a backup existed at the moment the
        // installer was handed control. After that point this process is
        // on its way out and nothing it does can be relied on.
        Assert.True(
            f.Launcher.SampledAtLaunch > 0,
            "The installer was started before any backup file had been written.");
    }

    /// <summary>
    /// A backup directory that cannot be written to stands in for every
    /// reason a backup fails on a hospital PC — a full disk, a NAS that is
    /// not mounted, a pg_dump that is older than the server.
    /// </summary>
    [Fact]
    public async Task A_failed_backup_stops_the_update()
    {
        var f = Arrange(
            backupFirst: true,
            backupDirectory: Path.Combine(NewDirectory(), "\0invalid"));

        var result = await f.Service.InstallAsync(f.ManifestPath);

        Assert.False(result.Started);
        Assert.Equal(0, f.Launcher.Calls);
        Assert.Contains("backup", result.Message!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing has been installed", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One impatient double-click, two Inno installers racing to replace the
    /// same files. The first handover does not stop the service instantly, so
    /// there is a real window of a few seconds in which a second request
    /// arrives and is otherwise honoured.
    ///
    /// Both fixtures are arranged before either is installed, deliberately:
    /// arranging resets the guard, so building the second one midway would
    /// clear exactly the state under test.
    /// </summary>
    [Fact]
    public async Task A_second_install_is_refused_while_one_is_already_running()
    {
        var first = Arrange(version: "9999.2.0");
        var second = Arrange(version: "9999.3.0");

        var one = await first.Service.InstallAsync(first.ManifestPath);
        Assert.True(one.Started, one.Message);

        var two = await second.Service.InstallAsync(second.ManifestPath);

        Assert.False(two.Started);
        Assert.Equal(0, second.Launcher.Calls);
        Assert.Contains("already being installed", two.Message!, StringComparison.Ordinal);

        // The staged copy of the refused update is cleaned up rather than left
        // behind: it is a couple of hundred megabytes on a hospital PC.
        Assert.Empty(Directory.GetFiles(second.StagingDirectory, "*.exe"));
    }

    /// <summary>
    /// Reached by reflection rather than by adding a reset method to the
    /// production type. A service that could un-hand-over would be a service
    /// that can launch two installers, which is the whole thing being
    /// prevented.
    /// </summary>
    private static void ResetHandoverGuard() =>
        typeof(UpdateService)
            .GetField("_handedOver", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, 0);

    /// <summary>
    /// Mirrors the escape hatch in BackupTests. A CI machine that quietly
    /// lost pg_dump would otherwise show a green suite that proves nothing
    /// about the backup half of this.
    /// </summary>
    private static bool ToolsUsable(string directory)
    {
        var locator = new PgToolLocator(Options.Create(new BackupOptions { Directory = directory }));
        var tool = locator.FindPgDump(new Version(17, 0));

        if (!tool.IsUsable &&
            Environment.GetEnvironmentVariable("HOSPITALPM_REQUIRE_PGDUMP") == "1")
        {
            Assert.Fail(
                "HOSPITALPM_REQUIRE_PGDUMP is set but pg_dump is not usable, so the pre-update "
                + $"backup test would prove nothing. {tool.Problem}");
        }

        return tool.IsUsable;
    }
}
