using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Licensing;
using HospitalPm.Domain.Locations;
using HospitalPm.Infrastructure.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The signed lock and unlock code, checked without a database: what it is, who can make one, and that a licence
/// and a code can never be taken for each other.
/// </summary>
public sealed class LicenceCodeTests : IDisposable
{
    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private string PublicKey => Convert.ToBase64String(_signer.ExportSubjectPublicKeyInfo());

    public void Dispose()
    {
        _signer.Dispose();
        _stranger.Dispose();
    }

    private static LicenceCommand Command(string action = LicenceAction.Lock, long sequence = 1, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), action, sequence, new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void A_code_signed_by_us_reads_back_exactly()
    {
        var command = Command(LicenceAction.Unlock, 7);

        var read = LicenceCommandFile.Verify(LicenceCommandFile.Sign(command, _signer), PublicKey);

        Assert.Equal(command, read);
    }

    [Fact]
    public void A_code_survives_being_pasted_with_spaces_and_a_different_line_ending()
    {
        var code = LicenceCommandFile.Sign(Command(), _signer);
        var mangled = "\n  " + code.Replace("\r\n", "\n").Replace("\n", "  \r\n   ") + "\n\n";

        Assert.NotNull(LicenceCommandFile.Verify(mangled, PublicKey));
    }

    [Fact]
    public void A_code_signed_by_anyone_else_is_refused()
    {
        var code = LicenceCommandFile.Sign(Command(), _stranger);

        Assert.Null(LicenceCommandFile.Verify(code, PublicKey));
    }

    [Fact]
    public void A_code_with_one_character_changed_is_refused()
    {
        var code = LicenceCommandFile.Sign(Command(), _signer);
        var lines = code.Split('\n').ToList();

        // The payload: change a character in the middle of the first block of base64.
        var payload = lines[1];
        var flipped = payload[..10] + (payload[10] == 'A' ? 'B' : 'A') + payload[11..];
        lines[1] = flipped;

        Assert.Null(LicenceCommandFile.Verify(string.Join('\n', lines), PublicKey));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("just some text")]
    [InlineData("-----BEGIN HOSPITALPM CODE-----\n-----SIGNATURE-----\n-----END HOSPITALPM CODE-----")]
    public void Anything_that_is_not_a_code_is_refused_without_a_crash(string? text)
    {
        Assert.Null(LicenceCommandFile.Verify(text, PublicKey));
    }

    [Fact]
    public void Nothing_is_accepted_when_the_build_has_no_public_key()
    {
        Assert.Null(LicenceCommandFile.Verify(LicenceCommandFile.Sign(Command(), _signer), string.Empty));
    }

    [Fact]
    public void A_licence_cannot_be_used_as_a_code_and_a_code_cannot_be_used_as_a_licence()
    {
        var licence = new Licence(Guid.NewGuid(), "Sahyadri Hospital, Pune", new DateOnly(2026, 1, 1), null, [], null, null);
        var payload = LicenceFile.Serialise(licence);
        var licenceText = LicenceFile.Format(
            payload, _signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        var codeText = LicenceCommandFile.Sign(Command(), _signer);

        // By the file markers alone.
        Assert.Null(LicenceCommandFile.Verify(licenceText, PublicKey));
        Assert.Equal(LicenceState.Invalid, new LicenceVerifier(PublicKey).Verify(codeText, new DateOnly(2026, 10, 5)).State);

        // And even with the markers swapped: a signature for one does not check as the other.
        var licenceInCodeWrapper = LicenceFile.Format(
            payload, _signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence), LicenceCommandFile.Kind);
        Assert.Null(LicenceCommandFile.Verify(licenceInCodeWrapper, PublicKey));

        var command = Command();
        var commandPayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(command, LicenceFile.Json);
        var signedAsLicence = LicenceFile.Format(
            commandPayload, _signer.SignData(commandPayload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Assert.Null(LicenceCommandFile.Verify(signedAsLicence.Replace("LICENCE", "CODE"), PublicKey));
    }

    [Theory]
    [InlineData("reboot", 1)]
    [InlineData("lock", 0)]
    [InlineData("lock", -3)]
    public void A_signed_code_that_says_something_meaningless_is_still_refused(string action, long sequence)
    {
        var code = LicenceCommandFile.Sign(new LicenceCommand(Guid.NewGuid(), action, sequence, DateTime.UtcNow), _signer);

        Assert.Null(LicenceCommandFile.Verify(code, PublicKey));
    }

    [Fact]
    public void A_code_for_no_licence_is_refused()
    {
        Assert.Null(LicenceCommandFile.Verify(LicenceCommandFile.Sign(Command(id: Guid.Empty), _signer), PublicKey));
    }
}

/// <summary>
/// What an installation does with a code: lock, unlock, and refuse what it should. Against real files, no database.
/// </summary>
public sealed class LicenceLockServiceTests : IDisposable
{
    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hospitalpm-lock-tests", Guid.NewGuid().ToString("N"));

    private string PublicKey => Convert.ToBase64String(_signer.ExportSubjectPublicKeyInfo());

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

    private string LicencePath => Path.Combine(_folder, "install", "hospitalpm.licence");

    private string MirrorFolder => Path.Combine(_folder, "data", "keys");

    private LicenceService Service() => new(
        Options.Create(new LicenceOptions { PublicKey = PublicKey, Path = LicencePath, LockMirrorDirectory = MirrorFolder }),
        TimeProvider.System);

    private string SignLicence(Licence licence)
    {
        var payload = LicenceFile.Serialise(licence);
        return LicenceFile.Format(payload, _signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    private Licence Install(LicenceService service, string name = "Sahyadri Hospital, Pune", int? max = null)
    {
        var licence = new Licence(Guid.NewGuid(), name, new DateOnly(2026, 1, 1), null, [], max, null);
        Assert.True(service.Install(SignLicence(licence)).Saved);
        return licence;
    }

    private string Code(Guid licenceId, string action, long sequence) =>
        LicenceCommandFile.Sign(new LicenceCommand(licenceId, action, sequence, DateTime.UtcNow), _signer);

    [Fact]
    public void A_new_installation_is_not_locked()
    {
        var service = Service();
        Install(service);

        Assert.Null(service.CurrentLock());
    }

    [Fact]
    public void A_lock_code_for_the_installed_licence_locks_it_and_names_the_hospital()
    {
        var service = Service();
        var licence = Install(service);

        var result = service.ApplyCode(Code(licence.LicenceId, LicenceAction.Lock, 1));

        Assert.True(result.Applied, result.Message);
        var state = service.CurrentLock();
        Assert.NotNull(state);
        Assert.Equal(licence.LicenceId, state.LicenceId);
        Assert.Equal("Sahyadri Hospital, Pune", state.HospitalName);
    }

    [Fact]
    public void An_unlock_code_with_a_higher_number_unlocks_it()
    {
        var service = Service();
        var licence = Install(service);
        service.ApplyCode(Code(licence.LicenceId, LicenceAction.Lock, 1));

        var result = service.ApplyCode(Code(licence.LicenceId, LicenceAction.Unlock, 2));

        Assert.True(result.Applied, result.Message);
        Assert.Null(service.CurrentLock());
    }

    [Fact]
    public void A_code_for_another_licence_does_nothing()
    {
        var service = Service();
        Install(service);

        var result = service.ApplyCode(Code(Guid.NewGuid(), LicenceAction.Lock, 1));

        Assert.False(result.Applied);
        Assert.Contains("different licence", result.Message, StringComparison.Ordinal);
        Assert.Null(service.CurrentLock());
    }

    [Fact]
    public void An_installation_with_no_licence_cannot_be_locked()
    {
        var service = Service();

        var result = service.ApplyCode(Code(Guid.NewGuid(), LicenceAction.Lock, 1));

        Assert.False(result.Applied);
        Assert.Null(service.CurrentLock());
    }

    [Fact]
    public void A_code_used_twice_does_nothing_the_second_time()
    {
        var service = Service();
        var licence = Install(service);
        var code = Code(licence.LicenceId, LicenceAction.Lock, 1);

        Assert.True(service.ApplyCode(code).Applied);
        var again = service.ApplyCode(code);

        Assert.False(again.Applied);
        Assert.Contains("already been used", again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_old_unlock_cannot_undo_a_newer_lock_and_an_old_lock_cannot_undo_a_newer_unlock()
    {
        var service = Service();
        var licence = Install(service);
        var lock1 = Code(licence.LicenceId, LicenceAction.Lock, 1);
        var unlock2 = Code(licence.LicenceId, LicenceAction.Unlock, 2);
        var lock3 = Code(licence.LicenceId, LicenceAction.Lock, 3);

        Assert.True(service.ApplyCode(lock1).Applied);
        Assert.True(service.ApplyCode(unlock2).Applied);
        Assert.Null(service.CurrentLock());

        // The lock that came before the unlock is no longer worth anything.
        Assert.False(service.ApplyCode(lock1).Applied);
        Assert.Null(service.CurrentLock());

        Assert.True(service.ApplyCode(lock3).Applied);
        Assert.NotNull(service.CurrentLock());

        // And the unlock that came before the newest lock cannot lift it.
        Assert.False(service.ApplyCode(unlock2).Applied);
        Assert.NotNull(service.CurrentLock());
    }

    [Fact]
    public void An_unlock_names_the_licence_that_locked_so_swapping_the_licence_file_does_not_slip_past()
    {
        var service = Service();
        var first = Install(service, "First Hospital");
        service.ApplyCode(Code(first.LicenceId, LicenceAction.Lock, 1));

        // Someone replaces the licence file with another valid one while it is locked.
        var second = Install(service, "Second Hospital");

        var wrong = service.ApplyCode(Code(second.LicenceId, LicenceAction.Unlock, 9));
        Assert.False(wrong.Applied);
        Assert.NotNull(service.CurrentLock());

        Assert.True(service.ApplyCode(Code(first.LicenceId, LicenceAction.Unlock, 2)).Applied);
        Assert.Null(service.CurrentLock());
    }

    [Fact]
    public void The_lock_is_remembered_across_a_restart()
    {
        var licence = Install(Service());
        Service().ApplyCode(Code(licence.LicenceId, LicenceAction.Lock, 1));

        Assert.NotNull(Service().CurrentLock());
    }

    [Fact]
    public void Deleting_one_of_the_two_places_does_not_unlock_and_the_missing_one_is_put_back()
    {
        var licence = Install(Service());
        Service().ApplyCode(Code(licence.LicenceId, LicenceAction.Lock, 1));

        var beside = LicencePath + ".lock";
        var mirror = Path.Combine(MirrorFolder, "licence-lock.json");
        Assert.True(File.Exists(beside));
        Assert.True(File.Exists(mirror));

        File.Delete(beside);
        Assert.NotNull(Service().CurrentLock());
        Assert.True(File.Exists(beside), "the place that was deleted is written again");

        File.Delete(mirror);
        Assert.NotNull(Service().CurrentLock());
        Assert.True(File.Exists(mirror));
    }

    [Fact]
    public void A_damaged_lock_file_does_not_unlock_while_the_other_place_says_locked()
    {
        var licence = Install(Service());
        Service().ApplyCode(Code(licence.LicenceId, LicenceAction.Lock, 1));

        File.WriteAllText(LicencePath + ".lock", "{ not json");

        Assert.NotNull(Service().CurrentLock());
    }

    [Fact]
    public void A_damaged_lock_file_and_nothing_else_is_not_a_lock()
    {
        // The limit, said plainly: with both places gone there is nothing to remember, and the installation opens.
        var licence = Install(Service());
        Service().ApplyCode(Code(licence.LicenceId, LicenceAction.Lock, 1));

        File.Delete(LicencePath + ".lock");
        File.Delete(Path.Combine(MirrorFolder, "licence-lock.json"));

        Assert.Null(Service().CurrentLock());
    }

    [Fact]
    public void A_code_is_refused_by_a_build_with_no_public_key()
    {
        var service = new LicenceService(
            Options.Create(new LicenceOptions { PublicKey = string.Empty, Path = LicencePath, LockMirrorDirectory = MirrorFolder }),
            TimeProvider.System);

        Assert.False(service.ApplyCode(Code(Guid.NewGuid(), LicenceAction.Lock, 1)).Applied);
    }

    [Fact]
    public void The_equipment_limit_is_the_licences_and_only_while_it_is_current()
    {
        var service = Service();
        Assert.Null(service.EquipmentLimit());

        Install(service, max: 25);
        Assert.Equal(25, service.EquipmentLimit());

        var uncapped = Service();
        Assert.True(uncapped.Install(SignLicence(new Licence(Guid.NewGuid(), "H", new DateOnly(2026, 1, 1), null, [], null, null))).Saved);
        Assert.Null(uncapped.EquipmentLimit());
    }
}

/// <summary>
/// The Developer's licence section and the lock, through the real API: who may use it, that a licence it makes
/// verifies, that a locked installation turns everyone away and an unlock lets them back, and the equipment cap.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class LicenceIssuingApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "LicenceIssue2026!";

    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hospitalpm-issue-tests", Guid.NewGuid().ToString("N"));

    private ApiFactory _factory = null!;
    private ApiFactory _withoutKey = null!;
    private HttpClient _developer = null!;
    private HttpClient _it = null!;
    private HttpClient _engineer = null!;
    private HttpClient _anonymous = null!;
    private string _suffix = null!;
    private int _typeId;
    private int _roomId;

    private string PublicKey => Convert.ToBase64String(_signer.ExportSubjectPublicKeyInfo());

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        var keyFile = Path.Combine(_folder, "signing-key.pem");
        await File.WriteAllTextAsync(keyFile, _signer.ExportPkcs8PrivateKeyPem());

        _factory = new ApiFactory(fixture.ConnectionString, new Dictionary<string, string?>
        {
            ["Licence:PublicKey"] = PublicKey,
            ["Licence:Path"] = Path.Combine(_folder, "install", "hospitalpm.licence"),
            ["Licence:SigningKeyPath"] = keyFile,
            ["Licence:LockMirrorDirectory"] = Path.Combine(_folder, "data", "keys"),
        });
        _withoutKey = new ApiFactory(fixture.ConnectionString, new Dictionary<string, string?>
        {
            ["Licence:PublicKey"] = PublicKey,
            ["Licence:Path"] = Path.Combine(_folder, "other", "hospitalpm.licence"),
            ["Licence:LockMirrorDirectory"] = Path.Combine(_folder, "other-data", "keys"),
        });

        await using (var db = fixture.CreateContext())
        {
            var room = new Location { Code = $"LI-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();
            _roomId = room.Id;
            _typeId = (await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator")).Id;
        }

        _developer = await SignInAsync(_factory, $"li-dev-{_suffix}", Roles.Developer);
        _it = await SignInAsync(_factory, $"li-it-{_suffix}", Roles.ItAdmin);
        _engineer = await SignInAsync(_factory, $"li-eng-{_suffix}", Roles.BmeEngineer);
        _anonymous = _factory.CreateClient();
    }

    private static async Task<HttpClient> SignInAsync(ApiFactory factory, string userName, string role)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser { UserName = userName, FullName = userName, IsActive = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, role);
        }

        var client = factory.CreateClient();
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
        _engineer?.Dispose();
        _anonymous?.Dispose();
        _factory?.Dispose();
        _withoutKey?.Dispose();
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

    private async Task<(int Id, Guid LicenceId, string Text)> IssueAsync(object? request = null)
    {
        var res = await _developer.PostAsJsonAsync("/api/developer/licences", request ?? new { hospitalName = "Sahyadri Hospital, Pune" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var licence = body.GetProperty("licence");
        return (licence.GetProperty("id").GetInt32(), licence.GetProperty("licenceId").GetGuid(), body.GetProperty("licenceText").GetString()!);
    }

    private async Task InstallAsync(string text) =>
        Assert.Equal(HttpStatusCode.OK, (await _developer.PostAsJsonAsync("/api/admin/licence", new { licence = text })).StatusCode);

    private async Task<string> CodeAsync(int id, string action)
    {
        var res = await _developer.PostAsync($"/api/developer/licences/{id}/{action}", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("no-store", res.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
    }

    private Task<HttpResponseMessage> EnterAsync(string code) =>
        _anonymous.PostAsJsonAsync("/api/licence/code", new { code });

    // ---------------------------------------------------------------- who may

    [Fact]
    public async Task Only_the_developer_may_use_the_licence_section()
    {
        foreach (var client in new[] { _it, _engineer })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/developer/licences")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/developer/licences", new { hospitalName = "X" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/developer/licences/1/lock", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/developer/licences/1/unlock", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/developer/licences/1/file")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.GetAsync("/api/developer/licences")).StatusCode);

        // And no grant can hand it out.
        Assert.False(PermissionCatalog.IsGrantable(Permissions.LicenceIssue));
        Assert.DoesNotContain(Permissions.LicenceIssue, RolePermissions.For(Roles.ItAdmin));
        Assert.DoesNotContain(Permissions.LicenceIssue, RolePermissions.For(Roles.BmeHead));
    }

    [Fact]
    public async Task A_copy_with_no_signing_key_says_so_and_issues_nothing()
    {
        using var developer = await SignInAsync(_withoutKey, $"li-nokey-{_suffix}", Roles.Developer);

        var list = await developer.GetFromJsonAsync<JsonElement>("/api/developer/licences");
        Assert.False(list.GetProperty("available").GetBoolean());
        Assert.Contains("no signing key", list.GetProperty("problem").GetString(), StringComparison.Ordinal);

        var issue = await developer.PostAsJsonAsync("/api/developer/licences", new { hospitalName = "X" });
        Assert.Equal(HttpStatusCode.Conflict, issue.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await developer.PostAsync("/api/developer/licences/1/lock", null)).StatusCode);
    }

    // ---------------------------------------------------------------- issuing

    [Fact]
    public async Task A_licence_issued_here_verifies_installs_and_can_be_sent_again()
    {
        var (id, licenceId, text) = await IssueAsync(new
        {
            hospitalName = "  Sahyadri Hospital, Pune ",
            durationDays = 84,
            maxEquipment = 500,
            modules = new[] { "Reports", "reports", "import-export" },
            notes = "pilot",
        });

        var status = new LicenceVerifier(PublicKey).Verify(text, DateOnly.FromDateTime(DateTime.UtcNow));
        Assert.Equal(LicenceState.Valid, status.State);
        Assert.Equal(licenceId, status.Licence!.LicenceId);
        Assert.Equal("Sahyadri Hospital, Pune", status.Licence.HospitalName);
        Assert.Equal(500, status.Licence.MaxEquipment);
        Assert.Equal(84, status.Licence.DurationDays);
        Assert.Equal(["reports", "import-export"], status.Licence.Modules);

        // On the list, and the file for it is the same signed text, not a second signature.
        var list = await _developer.GetFromJsonAsync<JsonElement>("/api/developer/licences");
        Assert.True(list.GetProperty("available").GetBoolean());
        Assert.Contains(list.GetProperty("licences").EnumerateArray(), l => l.GetProperty("id").GetInt32() == id);

        var file = await _developer.GetAsync($"/api/developer/licences/{id}/file");
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.EndsWith(".licence", file.Content.Headers.ContentDisposition?.FileName?.Trim('"'), StringComparison.Ordinal);
        Assert.Equal(text, await file.Content.ReadAsStringAsync());

        // The hospital installs it the way it installs any licence.
        await InstallAsync(text);
        var installed = await _developer.GetFromJsonAsync<JsonElement>("/api/admin/licence");
        Assert.Equal(licenceId, installed.GetProperty("licence").GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("""{ "hospitalName": "" }""")]
    [InlineData("""{ "hospitalName": "H", "durationDays": 10, "expiresOn": "2099-01-01" }""")]
    [InlineData("""{ "hospitalName": "H", "durationDays": 0 }""")]
    [InlineData("""{ "hospitalName": "H", "durationDays": 99999 }""")]
    [InlineData("""{ "hospitalName": "H", "maxEquipment": -1 }""")]
    [InlineData("""{ "hospitalName": "H", "expiresOn": "2001-01-01" }""")]
    [InlineData("""{ "hospitalName": "H", "modules": ["Bad Module!"] }""")]
    public async Task A_licence_that_makes_no_sense_is_refused_with_the_reason(string json)
    {
        var res = await _developer.PostAsync("/api/developer/licences", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace((await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()));
    }

    [Fact]
    public async Task Issuing_is_written_to_the_audit_log_by_the_database()
    {
        var (id, _, _) = await IssueAsync();

        await using var db = fixture.CreateContext();
        var rows = await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM audit_log WHERE table_name = 'issued_licence' AND record_pk = {id.ToString()}").ToListAsync();
        Assert.True(rows.Single() >= 1, "no audit row was written for the new licence");
    }

    // ---------------------------------------------------------------- the lock

    [Fact]
    public async Task A_lock_code_turns_everyone_away_and_an_unlock_code_lets_them_back()
    {
        var (id, _, text) = await IssueAsync();
        await InstallAsync(text);

        Assert.False((await _anonymous.GetFromJsonAsync<JsonElement>("/api/licence/lock")).GetProperty("locked").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await _engineer.GetAsync("/api/features")).StatusCode);

        // Both codes are made first, as they would be on our own copy: this installation stands in for the
        // hospital's, and once it is locked even the Developer's account here is turned away.
        var lockCode = await CodeAsync(id, "lock");
        var unlockCode = await CodeAsync(id, "unlock");

        // Entered by anyone, signed in or not: the signature is what counts.
        var entered = await EnterAsync(lockCode);
        Assert.Equal(HttpStatusCode.OK, entered.StatusCode);
        Assert.True((await entered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("locked").GetBoolean());

        // Everyone, including someone who was signed in a moment ago, and the Developer's own account.
        foreach (var client in new[] { _engineer, _developer, _it })
        {
            var refused = await client.GetAsync("/api/features");
            Assert.Equal(HttpStatusCode.Locked, refused.StatusCode);
            Assert.Equal("licence-locked", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }

        // Nobody can sign in, and nothing is written.
        var login = await _anonymous.PostAsJsonAsync("/api/auth/login", new { userName = $"li-eng-{_suffix}", password = Password });
        Assert.Equal(HttpStatusCode.Locked, login.StatusCode);
        Assert.Equal(HttpStatusCode.Locked, (await _developer.PostAsync("/api/admin/backups/run", null)).StatusCode);

        // The lock screen can still ask what is wrong, and is told who it is for.
        var screen = await _anonymous.GetFromJsonAsync<JsonElement>("/api/licence/lock");
        Assert.True(screen.GetProperty("locked").GetBoolean());
        Assert.Equal("Sahyadri Hospital, Pune", screen.GetProperty("hospitalName").GetString());
        Assert.Contains("locked", screen.GetProperty("message").GetString(), StringComparison.Ordinal);

        // The program itself is still up, for whoever has to answer the phone.
        Assert.Equal(HttpStatusCode.OK, (await _anonymous.GetAsync("/health")).StatusCode);

        // An unlock code, entered on that screen with nobody signed in, opens it again.
        var unlocked = await EnterAsync(unlockCode);
        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        Assert.False((await unlocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("locked").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await _engineer.GetAsync("/api/features")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _anonymous.PostAsJsonAsync("/api/auth/login", new { userName = $"li-eng-{_suffix}", password = Password })).StatusCode);
    }

    [Fact]
    public async Task Codes_are_numbered_so_an_old_one_cannot_undo_a_newer_one()
    {
        var (id, _, text) = await IssueAsync();
        await InstallAsync(text);

        var lock1 = await CodeAsync(id, "lock");
        var unlock2 = await CodeAsync(id, "unlock");
        var lock3 = await CodeAsync(id, "lock");
        var unlock4 = await CodeAsync(id, "unlock");

        Assert.Equal(HttpStatusCode.OK, (await EnterAsync(lock1)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnterAsync(unlock2)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await EnterAsync(lock3)).StatusCode);

        // The old unlock and the old lock are both spent. It stays locked.
        var replay = await EnterAsync(unlock2);
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Contains("already been used", (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnterAsync(lock1)).StatusCode);
        Assert.True((await _anonymous.GetFromJsonAsync<JsonElement>("/api/licence/lock")).GetProperty("locked").GetBoolean());

        // Our side keeps the counter, and what the last code made said. Read from the database: this installation is
        // locked, and so is its own API.
        await using (var db = fixture.CreateContext())
        {
            var row = await db.IssuedLicences.AsNoTracking().SingleAsync(l => l.Id == id);
            Assert.Equal(4, row.LockSequence);
            Assert.False(row.IsLocked);
        }

        Assert.Equal(HttpStatusCode.OK, (await EnterAsync(unlock4)).StatusCode);
        Assert.False((await _anonymous.GetFromJsonAsync<JsonElement>("/api/licence/lock")).GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task A_code_for_another_licence_or_a_forgery_or_a_licence_pasted_as_a_code_does_nothing()
    {
        var (_, _, text) = await IssueAsync();
        await InstallAsync(text);
        var (otherId, _, _) = await IssueAsync(new { hospitalName = "Another Hospital" });

        var other = await EnterAsync(await CodeAsync(otherId, "lock"));
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        Assert.Contains("different licence", (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), StringComparison.Ordinal);

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = LicenceCommandFile.Sign(new LicenceCommand(Guid.NewGuid(), LicenceAction.Lock, 1, DateTime.UtcNow), stranger);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnterAsync(forged)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnterAsync(text)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnterAsync("")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await EnterAsync(new string('x', 20_000))).StatusCode);

        Assert.False((await _anonymous.GetFromJsonAsync<JsonElement>("/api/licence/lock")).GetProperty("locked").GetBoolean());
    }

    // ---------------------------------------------------------------- the equipment limit

    [Fact]
    public async Task A_licence_with_a_cap_stops_the_machine_over_it_and_a_bigger_one_lets_it_through()
    {
        int recorded;
        await using (var db = fixture.CreateContext())
        {
            recorded = await db.Equipment.IgnoreQueryFilters().CountAsync();
        }

        var (_, _, capped) = await IssueAsync(new { hospitalName = "Small Hospital", maxEquipment = recorded + 1 });
        await InstallAsync(capped);

        var first = await _developer.PostAsJsonAsync("/api/equipment", new { equipmentTypeId = _typeId, locationId = _roomId });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await _developer.PostAsJsonAsync("/api/equipment", new { equipmentTypeId = _typeId, locationId = _roomId });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("licence-equipment-limit", body.GetProperty("code").GetString());
        Assert.Equal(recorded + 1, body.GetProperty("limit").GetInt32());
        Assert.Contains("larger licence", body.GetProperty("error").GetString(), StringComparison.Ordinal);

        // Reading and changing what is already there is not touched by the cap.
        Assert.Equal(HttpStatusCode.OK, (await _developer.GetAsync("/api/equipment")).StatusCode);

        var (_, _, bigger) = await IssueAsync(new { hospitalName = "Small Hospital", maxEquipment = recorded + 50 });
        await InstallAsync(bigger);
        Assert.Equal(HttpStatusCode.Created, (await _developer.PostAsJsonAsync("/api/equipment", new { equipmentTypeId = _typeId, locationId = _roomId })).StatusCode);

        var (_, _, uncapped) = await IssueAsync(new { hospitalName = "Big Hospital" });
        await InstallAsync(uncapped);
        Assert.Equal(HttpStatusCode.Created, (await _developer.PostAsJsonAsync("/api/equipment", new { equipmentTypeId = _typeId, locationId = _roomId })).StatusCode);
    }
}
