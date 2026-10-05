using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Operations;
using HospitalPm.Infrastructure.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Encrypted backups against the real database: the file is unreadable without the key, opens with it into an
/// archive pg_restore accepts, restores to the same data, and the plain backups of earlier versions are
/// encrypted and replaced. The pg_dump and pg_restore are the real ones, because the only claim worth making
/// about a backup is that it can be restored.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class BackupEncryptionTests(PostgresFixture fixture) : IDisposable
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
                // A leftover temp folder is not worth failing a test run.
            }
        }
    }

    private string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "hospitalpm-encryption-tests", Guid.NewGuid().ToString("N"));
        _directories.Add(path);
        return path;
    }

    private BackupService CreateService(BackupOptions options, BackupVault vault)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:HospitalPm"] = fixture.ConnectionString })
            .Build();
        var wrapped = Options.Create(options);

        return new BackupService(
            fixture.CreateContext(),
            new PgToolLocator(wrapped),
            vault,
            configuration,
            wrapped,
            TimeProvider.System,
            new HospitalPm.Infrastructure.Maintenance.HospitalClock(
                TimeProvider.System,
                Options.Create(new HospitalPm.Infrastructure.Maintenance.ScheduleOptions())),
            NullLogger<BackupService>.Instance);
    }

    private static bool ToolsUsable(BackupOptions options)
    {
        var locator = new PgToolLocator(Options.Create(options));
        var tool = locator.FindPgDump(new Version(18, 0));

        if (!tool.IsUsable && Environment.GetEnvironmentVariable("HOSPITALPM_REQUIRE_PGDUMP") == "1")
        {
            Assert.Fail($"HOSPITALPM_REQUIRE_PGDUMP is set but pg_dump is not usable, so these tests would prove nothing. {tool.Problem}");
        }

        return tool.IsUsable && locator.FindPgRestore(new Version(18, 0)).IsUsable;
    }

    // ---------------------------------------------------------------- the file

    [Fact]
    public async Task An_encrypted_backup_is_unreadable_without_the_key_and_opens_with_it_into_an_archive_pg_restore_accepts()
    {
        var options = new BackupOptions { Directory = NewDirectory() };
        var vault = TestVault.Create();

        var run = await CreateService(options, vault).RunAsync(BackupTrigger.Manual);

        if (!ToolsUsable(options))
        {
            Assert.Equal(BackupStatus.Failed, run.Status);
            return;
        }

        Assert.Equal(BackupStatus.Succeeded, run.Status);
        Assert.EndsWith(".dump.enc", run.FileName!, StringComparison.Ordinal);

        var path = Path.Combine(options.Directory, run.FileName!);
        var bytes = await File.ReadAllBytesAsync(path);

        // Nothing of the database is visible in the file: not the archive's marker, not a table name.
        Assert.True(BackupVault.LooksEncrypted(path));
        Assert.False(bytes.AsSpan().IndexOf("PGDMP"u8) >= 0, "the archive's marker is visible in the encrypted file");
        Assert.False(bytes.AsSpan().IndexOf("equipment_type"u8) >= 0, "a table name is visible in the encrypted file");
        Assert.Equal(run.SizeBytes, bytes.Length);

        // The plain dump was never on the disk: only the encrypted file is in the folder.
        Assert.Equal([run.FileName], Directory.GetFiles(options.Directory).Select(Path.GetFileName).ToArray());

        // Another machine, with no key, cannot open it.
        await Assert.ThrowsAsync<BackupDecryptionException>(() =>
            TestVault.Create().DecryptFileAsync(path, Path.Combine(options.Directory, "stolen.dump")));

        // This machine can, and what comes out is an archive pg_restore reads.
        var opened = Path.Combine(NewDirectory(), "opened.dump");
        Directory.CreateDirectory(Path.GetDirectoryName(opened)!);
        await vault.DecryptFileAsync(path, opened);

        var head = new byte[5];
        await using (var stream = File.OpenRead(opened))
        {
            _ = await stream.ReadAsync(head);
        }

        Assert.Equal("PGDMP"u8.ToArray(), head);

        var (exit, output, error) = await RunAsync(new PgToolLocator(Options.Create(options)).FindPgRestore(new Version(18, 0)).Path!, ["--list", opened]);
        Assert.True(exit == 0, error);
        Assert.Contains("equipment_type", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_encrypted_backup_restores_into_a_new_database_with_the_same_data()
    {
        var options = new BackupOptions { Directory = NewDirectory() };
        var vault = TestVault.Create();

        var run = await CreateService(options, vault).RunAsync(BackupTrigger.Manual);

        if (!ToolsUsable(options))
        {
            return;
        }

        Assert.Equal(BackupStatus.Succeeded, run.Status);

        var opened = Path.Combine(NewDirectory(), "opened.dump");
        Directory.CreateDirectory(Path.GetDirectoryName(opened)!);
        await vault.DecryptFileAsync(Path.Combine(options.Directory, run.FileName!), opened);

        var source = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        var scratch = $"hp_restore_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres", Pooling = false };

        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{scratch}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var pgRestore = new PgToolLocator(Options.Create(options)).FindPgRestore(new Version(18, 0)).Path!;
            var (exit, _, error) = await RunAsync(
                pgRestore,
                [$"--host={source.Host}", $"--port={source.Port}", $"--username={source.Username}", $"--dbname={scratch}", "--no-owner", opened],
                source.Password);
            Assert.True(exit == 0, error);

            var restored = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = scratch, Pooling = false };
            Assert.Equal(await CountAsync(fixture.ConnectionString, "equipment_type"), await CountAsync(restored.ConnectionString, "equipment_type"));
            Assert.Equal(await CountAsync(fixture.ConnectionString, "category"), await CountAsync(restored.ConnectionString, "category"));
            Assert.True(await CountAsync(restored.ConnectionString, "equipment_type") > 0);
        }
        finally
        {
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{scratch}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> CountAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(int Exit, string Output, string Error)> RunAsync(string exe, string[] arguments, string? password = null)
    {
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (password is not null) info.Environment["PGPASSWORD"] = password;

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    // ---------------------------------------------------------------- the plain backups already on the disk

    private static void Age(string path, TimeSpan by) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow - by);

    [Fact]
    public async Task Plain_backups_left_in_the_folder_are_encrypted_checked_and_replaced_and_the_record_follows()
    {
        var options = new BackupOptions { Directory = NewDirectory() };
        Directory.CreateDirectory(options.Directory);
        var vault = TestVault.Create();
        var service = CreateService(options, vault);

        var oldContent = RandomNumberGenerator.GetBytes(3000);
        var safetyContent = RandomNumberGenerator.GetBytes(1500);

        var older = Path.Combine(options.Directory, $"hospitalpm-20200101-{Guid.NewGuid():N}-IST.dump");
        var safety = Path.Combine(options.Directory, $"pre-restore-20200101-{Guid.NewGuid():N}.dump");
        var recent = Path.Combine(options.Directory, "hospitalpm-recent.dump");
        var bystander = Path.Combine(options.Directory, "somebody-elses.dump");
        await File.WriteAllBytesAsync(older, oldContent);
        await File.WriteAllBytesAsync(safety, safetyContent);
        await File.WriteAllBytesAsync(recent, [1, 2, 3]);
        await File.WriteAllBytesAsync(bystander, [4, 5, 6]);
        Age(older, TimeSpan.FromMinutes(5));
        Age(safety, TimeSpan.FromMinutes(5));
        Age(bystander, TimeSpan.FromMinutes(5));

        // The record of the older one, as the Backups page lists it.
        int runId;
        await using (var db = fixture.CreateContext())
        {
            var row = new BackupRun
            {
                StartedAtUtc = DateTime.UtcNow.AddYears(-6), Status = BackupStatus.Succeeded, Trigger = BackupTrigger.Scheduled,
                FileName = Path.GetFileName(older), SizeBytes = oldContent.Length,
            };
            db.BackupRuns.Add(row);
            await db.SaveChangesAsync();
            runId = row.Id;
        }

        var done = await service.EncryptLeftoversAsync();

        Assert.Equal(2, done);
        Assert.False(File.Exists(older), "the plain backup is still there");
        Assert.False(File.Exists(safety), "the plain safety copy is still there");

        // What is in the encrypted copies is exactly what was in the plain ones.
        Assert.Equal(oldContent, await DecryptAsync(vault, older + ".enc"));
        Assert.Equal(safetyContent, await DecryptAsync(vault, safety + ".enc"));

        // The record follows the file, so a restore from the list still finds it.
        await using (var db = fixture.CreateContext())
        {
            var stored = await db.BackupRuns.AsNoTracking().SingleAsync(r => r.Id == runId);
            Assert.Equal(Path.GetFileName(older) + ".enc", stored.FileName);
            Assert.Equal(new FileInfo(older + ".enc").Length, stored.SizeBytes);
        }

        // A file written in the last half minute may still be being written, and one that is not ours is not ours.
        Assert.True(File.Exists(recent));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(recent));
        Assert.True(File.Exists(bystander));

        // Nothing is left to do the second time.
        Assert.Equal(0, await service.EncryptLeftoversAsync());
        Assert.Empty(Directory.GetFiles(options.Directory, "*.partial"));
    }

    private static async Task<byte[]> DecryptAsync(BackupVault vault, string path)
    {
        await using var input = File.OpenRead(path);
        await using var decryptor = vault.OpenDecryptor(input, leaveOpen: true);
        using var output = new MemoryStream();
        await decryptor.CopyToAsync(output);
        return output.ToArray();
    }

    [Fact]
    public async Task A_plain_backup_that_already_has_an_encrypted_copy_is_left_alone()
    {
        var options = new BackupOptions { Directory = NewDirectory() };
        Directory.CreateDirectory(options.Directory);
        var service = CreateService(options, TestVault.Create());

        var plain = Path.Combine(options.Directory, $"hospitalpm-20200101-{Guid.NewGuid():N}-IST.dump");
        await File.WriteAllBytesAsync(plain, [1, 2, 3]);
        await File.WriteAllBytesAsync(plain + ".enc", [9, 9, 9]);
        Age(plain, TimeSpan.FromMinutes(5));

        Assert.Equal(0, await service.EncryptLeftoversAsync());

        // Neither is touched: which of the two is right is not for a tidy-up to decide.
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(plain));
        Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(plain + ".enc"));
    }
}

/// <summary>The keys, through the API: who may make a recovery key, what is shown, and that it works end to end.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class BackupEncryptionEndpointTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "Encrypt2026!";

    private ApiFactory _factory = null!;
    private HttpClient _it = null!;
    private HttpClient _engineer = null!;
    private HttpClient _itTeam = null!;
    private HttpClient _anonymous = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _anonymous = _factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        _it = await SignInAsync($"enc-dev-{suffix}", Roles.Developer);
        _engineer = await SignInAsync($"enc-eng-{suffix}", Roles.BmeEngineer);
        _itTeam = await SignInAsync($"enc-it-{suffix}", Roles.ItAdmin);
    }

    private async Task<HttpClient> SignInAsync(string userName, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
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
        _it?.Dispose();
        _engineer?.Dispose();
        _itTeam?.Dispose();
        _anonymous?.Dispose();
        _factory?.Dispose();
    }

    private async Task<JsonElement> EncryptionStatusAsync()
    {
        var res = await _it.GetAsync("/api/admin/backups");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("encryption");
    }

    private async Task<string> NewRecoveryKeyAsync()
    {
        var res = await _it.PostAsync("/api/admin/backups/encryption/recovery-key", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryKey").GetString()!;
    }

    [Fact]
    public async Task The_status_says_encryption_is_on_and_the_recovery_key_has_not_been_written_down()
    {
        var status = await EncryptionStatusAsync();

        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.False(status.GetProperty("recoveryKeySaved").GetBoolean());
    }

    [Fact]
    public async Task A_new_recovery_key_is_shown_once_never_cached_and_never_in_the_status()
    {
        var res = await _it.PostAsync("/api/admin/backups/encryption/recovery-key", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("no-store", res.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var key = body.GetProperty("recoveryKey").GetString()!;
        Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{4}){12}$", key);

        var status = await EncryptionStatusAsync();
        Assert.True(status.GetProperty("keyPresent").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, status.GetProperty("recoveryKeyCreatedAtUtc").ValueKind);
        Assert.False(status.GetProperty("recoveryKeySaved").GetBoolean());

        var whole = await (await _it.GetAsync("/api/admin/backups")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(key, whole, StringComparison.Ordinal);
        Assert.DoesNotContain(key.Replace("-", ""), whole, StringComparison.Ordinal);

        // Another one is a different key.
        Assert.NotEqual(key, await NewRecoveryKeyAsync());
    }

    [Fact]
    public async Task Saying_it_is_written_down_is_remembered_until_a_new_key_is_made()
    {
        await NewRecoveryKeyAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await _it.PostAsync("/api/admin/backups/encryption/recovery-key/saved", null)).StatusCode);
        Assert.True((await EncryptionStatusAsync()).GetProperty("recoveryKeySaved").GetBoolean());

        await NewRecoveryKeyAsync();
        Assert.False((await EncryptionStatusAsync()).GetProperty("recoveryKeySaved").GetBoolean());
    }

    [Fact]
    public async Task Only_those_who_hold_the_backups_section_can_make_or_confirm_a_key()
    {
        foreach (var path in new[] { "/api/admin/backups/encryption/recovery-key", "/api/admin/backups/encryption/recovery-key/saved" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await _engineer.PostAsync(path, null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.PostAsync(path, null)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await _engineer.GetAsync("/api/admin/backups")).StatusCode);

        // Nor does the hospital's own IT team: backups are the Developer's alone.
        foreach (var path in new[] { "/api/admin/backups/encryption/recovery-key", "/api/admin/backups/run" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await _itTeam.PostAsync(path, null)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await _itTeam.GetAsync("/api/admin/backups")).StatusCode);
    }

    [Fact]
    public async Task The_recovery_key_written_down_opens_a_backup_made_before_it_on_another_machine_and_the_old_one_stops_working()
    {
        var first = await NewRecoveryKeyAsync();

        var run = await _it.PostAsync("/api/admin/backups/run", null);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var result = await run.Content.ReadFromJsonAsync<JsonElement>();

        if (result.GetProperty("status").GetInt32() != (int)BackupStatus.Succeeded)
        {
            // No usable pg_dump here: the backup says why, and there is no file to open.
            Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("error").GetString()));
            return;
        }

        var directory = (await (await _it.GetAsync("/api/admin/backups")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("directory").GetString()!;
        var path = Path.Combine(directory, result.GetProperty("fileName").GetString()!);
        Assert.EndsWith(".dump.enc", path, StringComparison.Ordinal);

        var elsewhere = TestVault.Create();
        var output = Path.Combine(Path.GetTempPath(), $"hospitalpm-opened-{Guid.NewGuid():N}.dump");
        try
        {
            // Made with the first key.
            await elsewhere.DecryptFileAsync(path, output, first);
            Assert.True(new FileInfo(output).Length > 0);

            // Replace it: the file on disk is re-wrapped, so the new key opens it and the old one no longer does.
            var second = await NewRecoveryKeyAsync();
            await elsewhere.DecryptFileAsync(path, output, second);
            await Assert.ThrowsAsync<BackupDecryptionException>(() => elsewhere.DecryptFileAsync(path, output + "2", first));
        }
        finally
        {
            File.Delete(output);
            File.Delete(output + "2");
        }
    }

    // ---------------------------------------------------------------- taking a backup away and bringing one in

    /// <summary>A backup made through the API, or null when this machine has no usable pg_dump to make one.</summary>
    private async Task<(int Id, string Path, string FileName)?> BackUpAsync()
    {
        var run = await _it.PostAsync("/api/admin/backups/run", null);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var result = await run.Content.ReadFromJsonAsync<JsonElement>();
        if (result.GetProperty("status").GetInt32() != (int)BackupStatus.Succeeded)
        {
            return null;
        }

        var directory = (await (await _it.GetAsync("/api/admin/backups")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("directory").GetString()!;
        var fileName = result.GetProperty("fileName").GetString()!;
        return (result.GetProperty("id").GetInt32(), Path.Combine(directory, fileName), fileName);
    }

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] bytes, string? name = null, string? recoveryKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/backups/upload") { Content = new ByteArrayContent(bytes) };
        if (name is not null) request.Headers.Add("X-File-Name", Uri.EscapeDataString(name));
        if (recoveryKey is not null) request.Headers.Add("X-Recovery-Key", recoveryKey);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task A_download_link_is_for_the_developer_works_once_and_hands_over_the_encrypted_file()
    {
        var made = await BackUpAsync();
        if (made is null) return;
        var (id, path, fileName) = made.Value;

        var link = $"/api/admin/backups/{id}/download-link";
        Assert.Equal(HttpStatusCode.Forbidden, (await _engineer.PostAsync(link, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _itTeam.PostAsync(link, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.PostAsync(link, null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _it.PostAsync("/api/admin/backups/2000000000/download-link", null)).StatusCode);

        var asked = await _it.PostAsync(link, null);
        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        var url = (await asked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;

        // The browser follows the link with no sign-in of its own: the pass in it is the sign-in.
        var file = await _anonymous.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(fileName, file.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Contains("no-store", file.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var bytes = await file.Content.ReadAsByteArrayAsync();
        Assert.Equal("HPBK"u8.ToArray(), bytes[..4]);
        Assert.Equal(await File.ReadAllBytesAsync(path), bytes);

        // Used up. Nor does a made-up pass open anything.
        Assert.Equal(HttpStatusCode.NotFound, (await _anonymous.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _anonymous.GetAsync("/api/admin/backups/download/" + new string('a', 64))).StatusCode);
    }

    [Fact]
    public async Task A_backup_file_brought_in_is_checked_kept_and_listed_and_anything_else_is_refused()
    {
        var made = await BackUpAsync();
        if (made is null) return;
        var (_, path, _) = made.Value;
        var bytes = await File.ReadAllBytesAsync(path);
        const string name = "hospitalpm-20200101-000000-IST.dump.enc";

        // Nobody else may bring a file in, and someone not signed in is turned away first.
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(_engineer, bytes, name)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(_itTeam, bytes, name)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await UploadAsync(_anonymous, bytes, name)).StatusCode);

        // Not a backup at all, a backup with one byte changed, and one cut short: each is refused with a reason.
        var notBackup = await UploadAsync(_it, "just some text"u8.ToArray(), name);
        Assert.Equal(HttpStatusCode.BadRequest, notBackup.StatusCode);
        Assert.Contains("not an encrypted", (await notBackup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), StringComparison.Ordinal);

        var flipped = (byte[])bytes.Clone();
        flipped[flipped.Length / 2] ^= 0xFF;
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(_it, flipped, name)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(_it, bytes[..(bytes.Length - 40)], name)).StatusCode);

        // Nothing refused was kept, and nothing half-written was left behind.
        var directory = Path.GetDirectoryName(path)!;
        Assert.False(File.Exists(Path.Combine(directory, name)));
        Assert.Empty(Directory.GetFiles(directory, "*.partial"));

        // The real thing is kept under its own name, recorded, and offered for restore.
        var good = await UploadAsync(_it, bytes, name);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        var kept = await good.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(name, kept.GetProperty("fileName").GetString());
        Assert.Equal(bytes.Length, kept.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(directory, name)));

        var runs = (await (await _it.GetAsync("/api/admin/backups")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runs").EnumerateArray();
        Assert.Contains(runs, r => r.GetProperty("id").GetInt32() == kept.GetProperty("id").GetInt32() && r.GetProperty("fileName").GetString() == name);

        // The same name again does not overwrite it.
        var again = await (await UploadAsync(_it, bytes, name)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(name, again.GetProperty("fileName").GetString());
        Assert.EndsWith(".dump.enc", again.GetProperty("fileName").GetString(), StringComparison.Ordinal);

        // A name that is not one of ours, or that tries to leave the folder, is never used as given.
        var odd = await (await UploadAsync(_it, bytes, @"..\..\evil.exe")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("hospitalpm-", odd.GetProperty("fileName").GetString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(directory, "..", "evil.exe")));

        // It is a real record, so restoring it gets as far as the installed-system check and no further here.
        var restore = await _it.PostAsJsonAsync($"/api/admin/backups/{kept.GetProperty("id").GetInt32()}/restore", new { confirm = "RESTORE" });
        Assert.Equal(HttpStatusCode.BadRequest, restore.StatusCode);
        Assert.Contains("installed system", (await restore.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_backup_made_on_another_machine_is_taken_in_only_with_its_recovery_key()
    {
        var made = await BackUpAsync();
        if (made is null) return;

        // This machine's backup, opened with its own recovery key and sealed again by "another machine".
        var mine = await NewRecoveryKeyAsync();
        var sealedPath = Path.Combine(Path.GetTempPath(), $"hospitalpm-other-{Guid.NewGuid():N}");
        var plain = sealedPath + ".dump";
        var other = TestVault.Create();
        var otherKey = other.CreateRecoveryKey(null).Key;
        try
        {
            await TestVault.Create().DecryptFileAsync(made.Value.Path, plain, mine);
            await other.EncryptFileAsync(plain, sealedPath + ".enc");
            var bytes = await File.ReadAllBytesAsync(sealedPath + ".enc");

            // This machine's key does not open it, and the answer says the recovery key would.
            var without = await UploadAsync(_it, bytes);
            Assert.Equal(HttpStatusCode.BadRequest, without.StatusCode);
            Assert.True((await without.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("needsRecoveryKey").GetBoolean());

            // A different key does not open it either.
            Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(_it, bytes, null, mine)).StatusCode);

            // The key it was made with does, typed the way it is written down.
            var right = await UploadAsync(_it, bytes, null, otherKey.ToLowerInvariant().Replace("-", " "));
            Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        }
        finally
        {
            File.Delete(plain);
            File.Delete(sealedPath + ".enc");
        }
    }
}

/// <summary>
/// There is no way to write a plain backup. A settings file that still says Backup:Encrypt=false is ignored, and the
/// backup that comes out is encrypted all the same.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class NoPlainBackupTests(PostgresFixture fixture) : IDisposable
{
    private readonly ApiFactory _factory = new(fixture.ConnectionString, new Dictionary<string, string?> { ["Backup:Encrypt"] = "false" });

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task A_setting_that_asks_for_plain_backups_is_ignored_and_the_backup_is_still_encrypted()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser { UserName = $"np-{suffix}", FullName = "No Plain", IsActive = true };
            Assert.True((await users.CreateAsync(user, "NoPlain2026!")).Succeeded);
            await users.AddToRoleAsync(user, Roles.Developer);
        }

        using var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = $"np-{suffix}", password = "NoPlain2026!" });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        var status = await client.GetFromJsonAsync<JsonElement>("/api/admin/backups");
        Assert.True(status.GetProperty("encryption").GetProperty("enabled").GetBoolean());

        var run = await client.PostAsync("/api/admin/backups/run", null);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        var result = await run.Content.ReadFromJsonAsync<JsonElement>();
        if (result.GetProperty("status").GetInt32() != (int)BackupStatus.Succeeded)
        {
            // No usable pg_dump here: there is no file, and so no plain file either.
            return;
        }

        var directory = status.GetProperty("directory").GetString()!;
        var name = result.GetProperty("fileName").GetString()!;
        Assert.EndsWith(".dump.enc", name, StringComparison.Ordinal);
        Assert.True(BackupVault.LooksEncrypted(Path.Combine(directory, name)));
        Assert.Empty(Directory.GetFiles(directory, "*.dump"));
    }
}
