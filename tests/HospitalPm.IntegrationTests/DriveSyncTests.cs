using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Licensing;
using HospitalPm.Infrastructure.Licensing;
using HospitalPm.Infrastructure.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Sending the encrypted backups to the drive: what is sent, in what order, what is refused, what is remembered, what
/// is removed from the drive, and that a failure never escapes. The first set runs against a fake rclone, so no network
/// is touched; the second runs the real rclone against a folder on this disk, which proves the commands themselves.
/// </summary>
public sealed class DriveSyncTests : IDisposable
{
    private const string Key = """{"type":"service_account","private_key":"-----BEGIN PRIVATE KEY-----\nSECRETSECRET\n-----END PRIVATE KEY-----\n","client_email":"backup@example.iam.gserviceaccount.com"}""";

    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hospitalpm-drive-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _signer.Dispose();
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder is not worth failing a test run.
        }
    }

    private string Backups => Path.Combine(_folder, "backups");

    private sealed class FakeRclone : IRcloneRunner
    {
        public List<(string Exe, string[] Args, Dictionary<string, string> Env)> Calls { get; } = [];

        public HashSet<string> Remote { get; } = new(StringComparer.Ordinal);

        public string? FailCopyOf { get; set; }

        public string FailMessage { get; set; } = "Failed to copy: googleapi: Error 403: storage quota exceeded";

        public bool FailCheck { get; set; }

        public Exception? Throws { get; set; }

        public Task<RcloneResult> RunAsync(string exe, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((exe, [.. arguments], new Dictionary<string, string>(environment)));
            if (Throws is not null) throw Throws;

            var verb = arguments[0];
            var target = verb == "copyto" ? arguments[2] : arguments.Count > 1 ? arguments[1] : string.Empty;
            switch (verb)
            {
                case "copyto":
                    if (FailCopyOf is not null && target.EndsWith(FailCopyOf, StringComparison.Ordinal))
                        return Task.FromResult(new RcloneResult(1, string.Empty, FailMessage));
                    Remote.Add(target[(target.IndexOf(':') + 1)..]);
                    return Task.FromResult(new RcloneResult(0, string.Empty, string.Empty));
                case "check":
                    return Task.FromResult(FailCheck
                        ? new RcloneResult(1, string.Empty, "1 differences found")
                        : new RcloneResult(0, string.Empty, string.Empty));
                case "lsf":
                    var folder = target[(target.IndexOf(':') + 1)..] + "/";
                    return Task.FromResult(new RcloneResult(0, string.Join('\n', Remote.Where(r => r.StartsWith(folder, StringComparison.Ordinal)).Select(r => r[folder.Length..])), string.Empty));
                case "deletefile":
                    Remote.Remove(target[(target.IndexOf(':') + 1)..]);
                    return Task.FromResult(new RcloneResult(0, string.Empty, string.Empty));
                default:
                    return Task.FromResult(new RcloneResult(2, string.Empty, "unknown command"));
            }
        }
    }

    private LicenceService Licensed(out Guid licenceId, bool install = true)
    {
        var publicKey = Convert.ToBase64String(_signer.ExportSubjectPublicKeyInfo());
        var service = new LicenceService(
            Options.Create(new LicenceOptions
            {
                PublicKey = publicKey,
                Path = Path.Combine(_folder, "install", "hospitalpm.licence"),
                LockMirrorDirectory = Path.Combine(_folder, "keys"),
            }),
            TimeProvider.System);

        var licence = new Licence(Guid.NewGuid(), "Sahyadri Hospital, Pune", new DateOnly(2026, 1, 1), null, [], null, null);
        licenceId = licence.LicenceId;
        if (install)
        {
            var payload = LicenceFile.Serialise(licence);
            Assert.True(service.Install(LicenceFile.Format(payload, _signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))).Saved);
        }

        return service;
    }

    private DriveSync Sync(LicenceService licences, FakeRclone runner, Action<DriveOptions>? tune = null)
    {
        // Any file stands in for rclone: the fake never runs it.
        var fakeExe = Path.Combine(_folder, OperatingSystem.IsWindows() ? "rclone.exe" : "rclone");
        Directory.CreateDirectory(_folder);
        if (!File.Exists(fakeExe)) File.WriteAllText(fakeExe, "not really rclone");

        var drive = new DriveOptions { Enabled = true, RclonePath = fakeExe, ServiceAccountJson = Key, RootFolderId = "ROOTID", KeepCount = 14 };
        tune?.Invoke(drive);
        return new DriveSync(
            Options.Create(new BackupOptions { Directory = Backups, Drive = drive }),
            licences, runner, TimeProvider.System, NullLogger<DriveSync>.Instance);
    }

    private async Task<string> BackupAsync(string stamp, bool encrypted = true)
    {
        Directory.CreateDirectory(Backups);
        var name = $"hospitalpm-{stamp}-IST.dump" + (encrypted ? ".enc" : string.Empty);
        var path = Path.Combine(Backups, name);
        if (encrypted)
        {
            var plain = path + ".plain";
            await File.WriteAllBytesAsync(plain, RandomNumberGenerator.GetBytes(2048));
            await TestVault.Create().EncryptFileAsync(plain, path);
            File.Delete(plain);
        }
        else
        {
            await File.WriteAllBytesAsync(path, "PGDMP plain database"u8.ToArray());
        }

        return name;
    }

    // ---------------------------------------------------------------- when it does nothing

    [Fact]
    public async Task It_is_off_unless_switched_on_and_runs_nothing()
    {
        var rclone = new FakeRclone();
        await BackupAsync("20261001-020000");

        var result = await Sync(Licensed(out _), rclone, d => d.Enabled = false).SyncAsync();

        Assert.False(result.Ran);
        Assert.Contains("switched off", result.Skipped, StringComparison.Ordinal);
        Assert.Empty(rclone.Calls);
    }

    [Fact]
    public async Task It_needs_a_licence_because_the_drive_folder_is_named_after_it()
    {
        var rclone = new FakeRclone();
        await BackupAsync("20261001-020000");

        var result = await Sync(Licensed(out _, install: false), rclone).SyncAsync();

        Assert.False(result.Ran);
        Assert.Contains("licence", result.Skipped, StringComparison.Ordinal);
        Assert.Empty(rclone.Calls);

        // A folder named in the settings is a person using their own drive: no licence is needed to name it.
        var named = await Sync(Licensed(out _, install: false), rclone, d => d.Folder = "my-own-folder").SyncAsync();
        Assert.True(named.Ran, named.Skipped);
        Assert.Equal(1, named.Sent);
        Assert.StartsWith("HPDRIVE:my-own-folder/", rclone.Calls.First(c => c.Args[0] == "copyto").Args[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_says_so_when_rclone_or_the_key_is_missing()
    {
        await BackupAsync("20261001-020000");

        var noRclone = await Sync(Licensed(out _), new FakeRclone(), d => d.RclonePath = Path.Combine(_folder, "nowhere", "rclone")).SyncAsync();
        Assert.Contains("rclone was not found", noRclone.Skipped, StringComparison.Ordinal);

        var noKey = await Sync(Licensed(out _), new FakeRclone(), d => d.ServiceAccountJson = null).SyncAsync();
        Assert.Contains("not signed in", noKey.Skipped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_locked_installation_sends_nothing()
    {
        var licences = Licensed(out var id);
        var code = LicenceCommandFile.Sign(new LicenceCommand(id, LicenceAction.Lock, 1, DateTime.UtcNow), _signer);
        Assert.True(licences.ApplyCode(code).Applied);
        await BackupAsync("20261001-020000");
        var rclone = new FakeRclone();

        var result = await Sync(licences, rclone).SyncAsync();

        Assert.False(result.Ran);
        Assert.Empty(rclone.Calls);
    }

    // ---------------------------------------------------------------- sending

    [Fact]
    public async Task It_sends_the_files_that_have_not_gone_newest_first_into_the_hospitals_own_folder_and_remembers_them()
    {
        var licences = Licensed(out var id);
        var older = await BackupAsync("20261001-020000");
        var newer = await BackupAsync("20261002-020000");
        var rclone = new FakeRclone();
        var sync = Sync(licences, rclone);

        var first = await sync.SyncAsync();

        Assert.True(first.Ran);
        Assert.Equal(2, first.Sent);
        Assert.Null(first.Error);
        var copies = rclone.Calls.Where(c => c.Args[0] == "copyto").ToList();
        Assert.Equal(2, copies.Count);
        Assert.EndsWith(newer, copies[0].Args[2], StringComparison.Ordinal);
        Assert.EndsWith(older, copies[1].Args[2], StringComparison.Ordinal);
        Assert.StartsWith($"HPDRIVE:{id:N}/", copies[0].Args[2], StringComparison.Ordinal);

        // What has gone is not sent again, and a new file is the only one that is.
        var again = await sync.SyncAsync();
        Assert.Equal(0, again.Sent);

        var third = await BackupAsync("20261003-020000");
        var next = await sync.SyncAsync();
        Assert.Equal(1, next.Sent);
        Assert.EndsWith(third, rclone.Calls.Last(c => c.Args[0] == "copyto").Args[2], StringComparison.Ordinal);

        var status = sync.Status();
        Assert.True(status.Ready);
        Assert.Equal(3, status.Uploaded);
        Assert.Equal(0, status.Pending);
        Assert.NotNull(status.LastUploadAtUtc);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task The_key_goes_to_rclone_in_its_environment_and_never_on_a_command_line()
    {
        var licences = Licensed(out _);
        await BackupAsync("20261001-020000");
        var rclone = new FakeRclone();

        await Sync(licences, rclone).SyncAsync();

        var call = rclone.Calls.First(c => c.Args[0] == "copyto");
        Assert.DoesNotContain(call.Args, a => a.Contains("SECRETSECRET", StringComparison.Ordinal) || a.Contains("service_account", StringComparison.Ordinal));
        Assert.Equal(Key, call.Env["RCLONE_CONFIG_HPDRIVE_SERVICE_ACCOUNT_CREDENTIALS"]);
        Assert.Equal("drive", call.Env["RCLONE_CONFIG_HPDRIVE_TYPE"]);
        Assert.Equal("ROOTID", call.Env["RCLONE_CONFIG_HPDRIVE_ROOT_FOLDER_ID"]);

        // And no configuration file is read or written for it.
        Assert.False(File.Exists(call.Env["RCLONE_CONFIG"]));
    }

    [Fact]
    public async Task Every_upload_is_checked_against_the_drive_and_one_that_does_not_match_is_not_counted_as_sent()
    {
        var licences = Licensed(out _);
        var name = await BackupAsync("20261001-020000");
        var rclone = new FakeRclone { FailCheck = true };
        var sync = Sync(licences, rclone);

        var result = await sync.SyncAsync();

        var check = rclone.Calls.First(c => c.Args[0] == "check").Args;
        Assert.Contains("--one-way", check);
        Assert.Equal(name, check[check.ToList().IndexOf("--include") + 1]);
        Assert.Equal(0, result.Sent);
        Assert.Contains("did not match", result.Error, StringComparison.Ordinal);
        Assert.Equal(1, sync.Status().Pending);

        // Matching on the next try, and then it counts.
        rclone.FailCheck = false;
        Assert.Equal(1, (await sync.SyncAsync()).Sent);
    }

    [Fact]
    public async Task An_existing_rclone_conf_and_remote_can_be_used_instead_of_building_one()
    {
        var licences = Licensed(out var id);
        await BackupAsync("20261001-020000");
        var conf = Path.Combine(_folder, "rclone.conf");
        await File.WriteAllTextAsync(conf, "[gdrive]\ntype = drive\n");
        var rclone = new FakeRclone();

        await Sync(licences, rclone, d =>
        {
            d.ServiceAccountJson = null;
            d.RcloneConfigFile = conf;
            d.RemoteName = "gdrive";
        }).SyncAsync();

        var call = rclone.Calls.First(c => c.Args[0] == "copyto");
        Assert.StartsWith($"gdrive:{id:N}/", call.Args[2], StringComparison.Ordinal);
        Assert.Equal(conf, call.Env["RCLONE_CONFIG"]);
        Assert.DoesNotContain(call.Env.Keys, k => k.StartsWith("RCLONE_CONFIG_", StringComparison.Ordinal));

        // A file that is not there, and a remote with no name, are said plainly.
        var missing = await Sync(licences, new FakeRclone(), d => { d.RcloneConfigFile = Path.Combine(_folder, "nope.conf"); d.RemoteName = "gdrive"; }).SyncAsync();
        Assert.Contains("no rclone configuration file", missing.Skipped, StringComparison.Ordinal);
        var unnamed = await Sync(licences, new FakeRclone(), d => { d.RcloneConfigFile = conf; d.RemoteName = null; }).SyncAsync();
        Assert.Contains("RemoteName", unnamed.Skipped, StringComparison.Ordinal);
    }

    private const string Token = """{"access_token":"ya29.ACCESSSECRET","token_type":"Bearer","refresh_token":"1//REFRESHSECRET","expiry":"2026-10-05T10:00:00Z"}""";

    [Fact]
    public async Task A_personal_accounts_token_is_used_with_only_the_narrow_scope_and_in_preference_to_a_service_account()
    {
        var licences = Licensed(out _);
        await BackupAsync("20261001-020000");
        var rclone = new FakeRclone();

        // Given as a file, with the service account key also set: the token wins.
        var file = Path.Combine(_folder, "token.json");
        await File.WriteAllTextAsync(file, "  " + Token + "\n");
        await Sync(licences, rclone, d => { d.TokenFile = file; d.ServiceAccountJson = Key; }).SyncAsync();

        var env = rclone.Calls.First(c => c.Args[0] == "copyto").Env;
        Assert.Equal("drive", env["RCLONE_CONFIG_HPDRIVE_TYPE"]);
        // Only the files this program creates itself: never the rest of the person's Drive.
        Assert.Equal("drive.file", env["RCLONE_CONFIG_HPDRIVE_SCOPE"]);
        Assert.Equal(Token, env["RCLONE_CONFIG_HPDRIVE_TOKEN"]);
        Assert.False(env.ContainsKey("RCLONE_CONFIG_HPDRIVE_SERVICE_ACCOUNT_CREDENTIALS"));
    }

    [Fact]
    public async Task A_token_never_appears_in_an_error_or_on_a_command_line()
    {
        var licences = Licensed(out _);
        var name = await BackupAsync("20261001-020000");
        var rclone = new FakeRclone { FailCopyOf = name, FailMessage = "couldn't refresh " + Token + " and " + "\"refresh_token\": \"1//OTHER\"" };

        var result = await Sync(licences, rclone, d => { d.TokenJson = Token; d.ServiceAccountJson = null; }).SyncAsync();

        Assert.NotNull(result.Error);
        Assert.DoesNotContain("REFRESHSECRET", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("ACCESSSECRET", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("OTHER", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(rclone.Calls.SelectMany(c => c.Args), a => a.Contains("SECRET", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_file_that_is_not_encrypted_is_never_sent_whatever_it_is_called()
    {
        var licences = Licensed(out _);
        await BackupAsync("20261001-020000", encrypted: false);

        // A plain dump named like ours, and a file named like an encrypted one that is not.
        var impostor = Path.Combine(Backups, "hospitalpm-20261002-020000-IST.dump.enc");
        await File.WriteAllBytesAsync(impostor, "PGDMP the whole database, in the clear"u8.ToArray());
        var rclone = new FakeRclone();

        var result = await Sync(licences, rclone).SyncAsync();

        Assert.Equal(0, result.Sent);
        Assert.DoesNotContain(rclone.Calls, c => c.Args[0] == "copyto");
    }

    [Fact]
    public async Task A_failed_upload_stops_the_run_says_why_without_the_key_and_is_tried_again_next_time()
    {
        var licences = Licensed(out _);
        var older = await BackupAsync("20261001-020000");
        var newer = await BackupAsync("20261002-020000");
        var rclone = new FakeRclone { FailCopyOf = newer, FailMessage = "Failed to copy: 403 " + Key };
        var sync = Sync(licences, rclone);

        var result = await sync.SyncAsync();

        Assert.True(result.Ran);
        Assert.Equal(0, result.Sent);
        Assert.NotNull(result.Error);
        Assert.DoesNotContain("SECRETSECRET", result.Error, StringComparison.Ordinal);
        Assert.Contains("[key removed]", result.Error, StringComparison.Ordinal);

        // It stopped at the first failure: the older file was not tried, and nothing is marked as gone.
        Assert.Single(rclone.Calls.Where(c => c.Args[0] == "copyto"));
        var status = sync.Status();
        Assert.Equal(0, status.Uploaded);
        Assert.Equal(2, status.Pending);
        Assert.Equal(result.Error, status.LastError);
        Assert.Null(status.LastUploadAtUtc);

        // The next night it works, sends both, and the error clears.
        rclone.FailCopyOf = null;
        var retry = await sync.SyncAsync();
        Assert.Equal(2, retry.Sent);
        Assert.Null(sync.Status().LastError);
        Assert.Contains(older, rclone.Remote.Select(r => r[(r.LastIndexOf('/') + 1)..]));
    }

    [Fact]
    public async Task Rclone_going_missing_or_breaking_is_an_error_on_the_page_and_never_an_exception()
    {
        var licences = Licensed(out _);
        await BackupAsync("20261001-020000");
        var rclone = new FakeRclone { Throws = new InvalidOperationException("rclone could not be started.") };
        var sync = Sync(licences, rclone);

        var result = await sync.SyncAsync();

        Assert.Contains("could not be started", result.Error, StringComparison.Ordinal);
        Assert.Contains("could not be started", sync.Status().LastError, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- keeping the drive tidy

    [Fact]
    public async Task Only_the_newest_few_are_kept_on_the_drive_and_nothing_else_there_is_touched()
    {
        var licences = Licensed(out var id);
        var rclone = new FakeRclone();
        var folder = $"{id:N}";

        // The drive already holds five older backups, and a file of someone else's that happens to be in the folder.
        foreach (var day in new[] { "20260901", "20260902", "20260903", "20260904", "20260905" })
        {
            rclone.Remote.Add($"{folder}/hospitalpm-{day}-020000-IST.dump.enc");
        }

        rclone.Remote.Add($"{folder}/notes.txt");
        rclone.Remote.Add($"{folder}/hospitalpm-20260901-020000-IST.dump");

        await BackupAsync("20261001-020000");
        await Sync(licences, rclone, d => d.KeepCount = 3).SyncAsync();

        var kept = rclone.Remote.Select(r => r[(folder.Length + 1)..]).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(
        [
            "hospitalpm-20260901-020000-IST.dump", // not an encrypted backup of ours: left alone
            "hospitalpm-20260904-020000-IST.dump.enc",
            "hospitalpm-20260905-020000-IST.dump.enc",
            "hospitalpm-20261001-020000-IST.dump.enc",
            "notes.txt",
        ], kept);
    }

    // ---------------------------------------------------------------- the real rclone

    private static string? RealRclone()
    {
        var name = OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";
        var found = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name))
            .FirstOrDefault(File.Exists);

        if (found is null && Environment.GetEnvironmentVariable("HOSPITALPM_REQUIRE_RCLONE") == "1")
        {
            Assert.Fail("HOSPITALPM_REQUIRE_RCLONE is set but rclone is not on the PATH, so these tests would prove nothing.");
        }

        return found;
    }

    [Fact]
    public async Task The_real_rclone_copies_the_files_byte_for_byte_and_trims_the_old_ones_and_sends_nothing_twice()
    {
        var rclone = RealRclone();
        if (rclone is null) return;

        var licences = Licensed(out _);
        var remote = Path.Combine(_folder, "remote").Replace('\\', '/');
        var names = new[]
        {
            await BackupAsync("20261001-020000"),
            await BackupAsync("20261002-020000"),
            await BackupAsync("20261003-020000"),
        };

        // A plain folder stands in for the drive: same commands, same environment settings, no network.
        var sync = new DriveSync(
            Options.Create(new BackupOptions
            {
                Directory = Backups,
                Drive = new DriveOptions { Enabled = true, RclonePath = rclone, Folder = remote, KeepCount = 2, Remote = new() { ["type"] = "local" } },
            }),
            licences, new ProcessRcloneRunner(), TimeProvider.System, NullLogger<DriveSync>.Instance);

        var first = await sync.SyncAsync();

        Assert.True(first.Ran, first.Skipped);
        Assert.Null(first.Error);
        Assert.Equal(2, first.Sent); // only the newest two are kept, so only those are sent

        // What arrived is exactly what was sent, and only the newest two.
        var arrived = Directory.GetFiles(remote).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([names[1], names[2]], arrived);
        foreach (var name in arrived)
        {
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(Backups, name!)), await File.ReadAllBytesAsync(Path.Combine(remote, name!)));
        }

        // A third night: a new file goes, the oldest on the drive is removed, and nothing already sent is sent again.
        var fourth = await BackupAsync("20261004-020000");
        var next = await sync.SyncAsync();
        Assert.Equal(1, next.Sent);
        Assert.Equal([names[2], fourth], Directory.GetFiles(remote).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(0, (await sync.SyncAsync()).Sent);
        Assert.Null(sync.Status().LastError);
    }

    [Fact]
    public async Task The_real_rclone_works_through_an_existing_rclone_conf_and_its_check_passes()
    {
        var rclone = RealRclone();
        if (rclone is null) return;

        var licences = Licensed(out _);
        var remote = Path.Combine(_folder, "remote2").Replace('\\', '/');
        var conf = Path.Combine(_folder, "rclone.conf");
        await File.WriteAllTextAsync(conf, "[testremote]\ntype = local\n");
        var name = await BackupAsync("20261001-020000");

        var sync = new DriveSync(
            Options.Create(new BackupOptions
            {
                Directory = Backups,
                Drive = new DriveOptions { Enabled = true, RclonePath = rclone, RcloneConfigFile = conf, RemoteName = "testremote", Folder = remote },
            }),
            licences, new ProcessRcloneRunner(), TimeProvider.System, NullLogger<DriveSync>.Instance);

        var result = await sync.SyncAsync();

        Assert.True(result.Ran, result.Skipped);
        Assert.Null(result.Error);
        Assert.Equal(1, result.Sent);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(Backups, name)), await File.ReadAllBytesAsync(Path.Combine(remote, name)));
    }

    [Fact]
    public async Task The_real_rclone_failing_is_reported_without_throwing()
    {
        var rclone = RealRclone();
        if (rclone is null) return;

        var licences = Licensed(out _);
        await BackupAsync("20261001-020000");

        // A remote type rclone does not have: it refuses, and the message reaches the page.
        var sync = new DriveSync(
            Options.Create(new BackupOptions
            {
                Directory = Backups,
                Drive = new DriveOptions { Enabled = true, RclonePath = rclone, Folder = "x", Remote = new() { ["type"] = "no-such-kind-of-remote" } },
            }),
            licences, new ProcessRcloneRunner(), TimeProvider.System, NullLogger<DriveSync>.Instance);

        var result = await sync.SyncAsync();

        Assert.True(result.Ran);
        Assert.Equal(0, result.Sent);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Equal(1, sync.Status().Pending);
    }
}

/// <summary>The Backups page's view of the drive, through the API: who may press the button, and what it says when it is off.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class DriveSyncEndpointTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "DriveSync2026!";

    private ApiFactory _factory = null!;
    private HttpClient _developer = null!;
    private HttpClient _it = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        _developer = await SignInAsync($"drv-dev-{suffix}", Roles.Developer);
        _it = await SignInAsync($"drv-it-{suffix}", Roles.ItAdmin);
    }

    private async Task<HttpClient> SignInAsync(string userName, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser { UserName = userName, FullName = userName, IsActive = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, role);
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = Password });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        return client;
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _developer?.Dispose();
        _it?.Dispose();
        _factory?.Dispose();
    }

    [Fact]
    public async Task The_drive_is_off_by_default_and_the_page_says_so()
    {
        var status = (await _developer.GetFromJsonAsync<JsonElement>("/api/admin/backups")).GetProperty("drive");

        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("ready").GetBoolean());
        Assert.Contains("switched off", status.GetProperty("problem").GetString(), StringComparison.Ordinal);

        var sent = await _developer.PostAsync("/api/admin/backups/drive/sync", null);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var body = await sent.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("ran").GetBoolean());
        Assert.Contains("switched off", body.GetProperty("skipped").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_developer_may_send_to_the_drive()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _it.PostAsync("/api/admin/backups/drive/sync", null)).StatusCode);
    }
}
