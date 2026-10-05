using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.Maintenance;
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
/// The promise behind "press one button and everything is back": a photo is not a loose file beside the database,
/// it is a row in it, tied to its work order or its PM by a foreign key. So one backup carries the records, the
/// photos and the links between them, and a restore returns all three.
///
/// This restores a real backup into a scratch database with the real pg_restore and checks, byte for byte, that
/// each photo and PM report came back, is still attached to the same work order or PM, and is still tied to it by
/// a constraint the database enforces.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class BackupPhotoLinkTests(PostgresFixture fixture) : IDisposable
{
    private const string Password = "PhotoLink2026!";

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
        var path = Path.Combine(Path.GetTempPath(), "hospitalpm-photolink-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _directories.Add(path);
        return path;
    }

    /// <summary>Not a real photo, but starts like one and is as large as one: what the upload checks is the opening bytes.</summary>
    private static byte[] Photo(int length)
    {
        var bytes = RandomNumberGenerator.GetBytes(length);
        bytes[0] = 0xFF;
        bytes[1] = 0xD8;
        bytes[2] = 0xFF;
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    [Fact]
    public async Task A_restored_backup_returns_every_photo_and_PM_report_still_attached_to_the_same_record()
    {
        var options = new BackupOptions { Directory = NewDirectory(), Encrypt = true };
        var locator = new PgToolLocator(Options.Create(options));
        var tool = locator.FindPgDump(new Version(18, 0));
        if (!tool.IsUsable)
        {
            if (Environment.GetEnvironmentVariable("HOSPITALPM_REQUIRE_PGDUMP") == "1")
            {
                Assert.Fail($"HOSPITALPM_REQUIRE_PGDUMP is set but pg_dump is not usable. {tool.Problem}");
            }

            return;
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var factory = new ApiFactory(fixture.ConnectionString);

        // A machine, a fault with two photos, and a vendor's PM with a report.
        int equipmentId, userId;
        await using (var db = fixture.CreateContext())
        {
            var room = new Location { Code = $"PL-{suffix}", Name = $"Room {suffix}", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();
            var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
            var machine = new Domain.Assets.Equipment { AssetTag = $"PL-{suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id };
            db.Equipment.Add(machine);
            await db.SaveChangesAsync();
            equipmentId = machine.Id;
        }

        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser { UserName = $"pl-{suffix}", FullName = "Photo Link", IsActive = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, Roles.BmeHead);
            userId = user.Id;
        }

        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = $"pl-{suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        var created = await client.PostAsJsonAsync("/api/work-orders", new { equipmentId, faultDescription = "Cracked casing", priority = 20 });
        created.EnsureSuccessStatusCode();
        var workOrderId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        byte[][] photos = [Photo(180_000), Photo(95_000)];
        using (var form = new MultipartFormDataContent())
        {
            for (var i = 0; i < photos.Length; i++)
            {
                var part = new ByteArrayContent(photos[i]);
                part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(part, "files", $"crack-{i}.jpg");
            }

            (await client.PostAsync($"/api/work-orders/{workOrderId}/photos", form)).EnsureSuccessStatusCode();
        }

        var report = Photo(60_000);
        int taskId;
        await using (var db = fixture.CreateContext())
        {
            var template = new Domain.Checklists.ChecklistTemplate { EquipmentTypeId = (await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator")).Id, Code = $"pl-{suffix}", Name = "Photo link PM" };
            db.ChecklistTemplates.Add(template);
            await db.SaveChangesAsync();
            var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
            var schedule = new PmSchedule
            {
                EquipmentId = equipmentId, ChecklistTemplateId = template.Id, Frequency = PmFrequency.Yearly,
                AnchorDate = today.AddYears(3), GraceDays = 7, PerformedBy = PmPerformedBy.Vendor,
            };
            db.PmSchedules.Add(schedule);
            await db.SaveChangesAsync();
            var task = new PmTask { PmScheduleId = schedule.Id, EquipmentId = equipmentId, DueDate = today, Status = PmTaskStatus.Due };
            db.PmTasks.Add(task);
            await db.SaveChangesAsync();
            taskId = task.Id;

            db.PmTaskAttachments.Add(new PmTaskAttachment
            {
                PmTaskId = taskId, FileName = "vendor-report.jpg", ContentType = "image/jpeg", SizeBytes = report.Length,
                UploadedByUserId = userId, UploadedAtUtc = DateTime.UtcNow, Data = new PmTaskAttachmentData { Data = report },
            });
            await db.SaveChangesAsync();
        }

        // Back it up, with the real pg_dump and the real encryption.
        var vault = TestVault.Create();
        var wrapped = Options.Create(options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:HospitalPm"] = fixture.ConnectionString })
            .Build();
        var service = new BackupService(
            fixture.CreateContext(), locator, vault, configuration, wrapped, TimeProvider.System,
            new HospitalPm.Infrastructure.Maintenance.HospitalClock(
                TimeProvider.System, Options.Create(new HospitalPm.Infrastructure.Maintenance.ScheduleOptions())),
            NullLogger<BackupService>.Instance);

        var run = await service.RunAsync(BackupTrigger.Manual);
        Assert.Equal(BackupStatus.Succeeded, run.Status);

        // Nothing the app generates on demand is in the folder: one encrypted file, no reports, no PDFs.
        var files = Directory.GetFiles(options.Directory);
        Assert.Equal([run.FileName], files.Select(Path.GetFileName).ToArray());

        var opened = Path.Combine(NewDirectory(), "opened.dump");
        await vault.DecryptFileAsync(Path.Combine(options.Directory, run.FileName!), opened);

        // Restore it into an empty database, as a new machine would.
        var source = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        var scratch = $"hp_photos_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres", Pooling = false };
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{scratch}\"", connection);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var pgRestore = locator.FindPgRestore(new Version(18, 0)).Path!;
            var info = new ProcessStartInfo(pgRestore) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { $"--host={source.Host}", $"--port={source.Port}", $"--username={source.Username}", $"--dbname={scratch}", "--no-owner", opened })
            {
                info.ArgumentList.Add(argument);
            }

            info.Environment["PGPASSWORD"] = source.Password;
            using (var process = Process.Start(info)!)
            {
                var error = process.StandardError.ReadToEndAsync();
                _ = process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, await error);
            }

            var restored = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = scratch, Pooling = false };
            await using var db = new NpgsqlConnection(restored.ConnectionString);
            await db.OpenAsync();

            // The fault is there, and so are its photos, each tied to it and identical byte for byte.
            Assert.Equal(1L, await ScalarAsync(db, "SELECT count(*) FROM work_order WHERE id = @id", workOrderId));

            var found = new List<(string Name, string Hash, int Declared, int Actual)>();
            await using (var command = new NpgsqlCommand(
                "SELECT a.file_name, d.data, a.size_bytes FROM work_order_attachment a " +
                "JOIN work_order_attachment_data d ON d.attachment_id = a.id WHERE a.work_order_id = @id ORDER BY a.id", db))
            {
                command.Parameters.AddWithValue("id", workOrderId);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var data = (byte[])reader["data"];
                    found.Add((reader.GetString(0), Sha(data), reader.GetInt32(2), data.Length));
                }
            }

            Assert.Equal(2, found.Count);
            Assert.Equal(["crack-0.jpg", "crack-1.jpg"], found.Select(f => f.Name).Order().ToArray());
            Assert.Equal(photos.Select(Sha).Order().ToArray(), found.Select(f => f.Hash).Order().ToArray());
            Assert.All(found, f => Assert.Equal(f.Declared, f.Actual));

            // The vendor's PM report, too, still against the same PM.
            Assert.Equal(1L, await ScalarAsync(db, "SELECT count(*) FROM pm_task WHERE id = @id", taskId));
            await using (var command = new NpgsqlCommand(
                "SELECT d.data FROM pm_task_attachment a JOIN pm_task_attachment_data d ON d.attachment_id = a.id " +
                "WHERE a.pm_task_id = @id", db))
            {
                command.Parameters.AddWithValue("id", taskId);
                var data = (byte[]?)await command.ExecuteScalarAsync();
                Assert.NotNull(data);
                Assert.Equal(Sha(report), Sha(data!));
            }

            // And the link is not a convention: the restored database still enforces it.
            foreach (var (child, parent) in new[]
                     {
                         ("work_order_attachment", "work_order"), ("work_order_attachment_data", "work_order_attachment"),
                         ("pm_task_attachment", "pm_task"), ("pm_task_attachment_data", "pm_task_attachment"),
                     })
            {
                await using var command = new NpgsqlCommand(
                    "SELECT count(*) FROM pg_constraint c WHERE c.contype = 'f' " +
                    "AND c.conrelid = @child::regclass AND c.confrelid = @parent::regclass", db);
                command.Parameters.AddWithValue("child", child);
                command.Parameters.AddWithValue("parent", parent);
                Assert.True((long)(await command.ExecuteScalarAsync())! >= 1, $"{child} is no longer tied to {parent}");
            }
        }
        finally
        {
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{scratch}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, int id)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
