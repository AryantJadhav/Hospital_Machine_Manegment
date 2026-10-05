using HospitalPm.Domain.Operations;
using HospitalPm.Infrastructure.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Backups, against the real database.
///
/// The dump is taken from the Testcontainers PostgreSQL and read back with
/// pg_restore, because the only claim worth making about a backup is that it
/// can be opened again. Asserting that pg_dump exited zero would pass on a
/// truncated file.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class BackupTests(PostgresFixture fixture) : IDisposable
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
        var path = Path.Combine(Path.GetTempPath(), "hospitalpm-backup-tests", Guid.NewGuid().ToString("N"));
        _directories.Add(path);
        return path;
    }

    private BackupService CreateService(BackupOptions options)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HospitalPm"] = fixture.ConnectionString,
            })
            .Build();

        var wrapped = Options.Create(options);

        return new BackupService(
            fixture.CreateContext(),
            new PgToolLocator(wrapped),
            TestVault.Create(),
            configuration,
            wrapped,
            TimeProvider.System,
            new HospitalPm.Infrastructure.Maintenance.HospitalClock(
                TimeProvider.System,
                Options.Create(new HospitalPm.Infrastructure.Maintenance.ScheduleOptions())),
            NullLogger<BackupService>.Instance);
    }

    /// <summary>
    /// pg_dump has to be present and at least as new as the server. Where it
    /// is not, the happy-path tests would be testing the test environment, so
    /// they assert the recorded failure instead.
    ///
    /// That escape hatch is dangerous on its own: a CI machine that quietly
    /// lost pg_dump would show a green suite that proves nothing about
    /// backups. HOSPITALPM_REQUIRE_PGDUMP closes it, and CI sets it.
    /// </summary>
    private static bool ToolsUsable(BackupOptions options)
    {
        var locator = new PgToolLocator(Options.Create(options));
        var tool = locator.FindPgDump(new Version(18, 0));

        if (!tool.IsUsable &&
            Environment.GetEnvironmentVariable("HOSPITALPM_REQUIRE_PGDUMP") == "1")
        {
            Assert.Fail(
                "HOSPITALPM_REQUIRE_PGDUMP is set but pg_dump is not usable, so the backup "
                + $"tests would prove nothing. {tool.Problem}");
        }

        return tool.IsUsable;
    }

    [Fact]
    public async Task A_plain_backup_is_written_and_can_be_read_back()
    {
        // Encryption is on by default and is tested in BackupEncryptionTests; this is the plain format, still
        // available for a hospital that encrypts the backup drive itself.
        var options = new BackupOptions { Directory = NewDirectory(), Encrypt = false };

        var service = CreateService(options);
        var run = await service.RunAsync(BackupTrigger.Manual);

        if (!ToolsUsable(options))
        {
            // No usable pg_dump here: the contract is that this is recorded as
            // a failure with a reason, never as a silent success.
            Assert.Equal(BackupStatus.Failed, run.Status);
            Assert.False(string.IsNullOrWhiteSpace(run.Error));
            Assert.Contains("pg_dump", run.Error!, StringComparison.OrdinalIgnoreCase);
            return;
        }

        Assert.Equal(BackupStatus.Succeeded, run.Status);
        Assert.Null(run.Error);
        Assert.NotNull(run.FileName);
        Assert.True(run.SizeBytes > 0, "the dump file is empty");

        var path = Path.Combine(options.Directory, run.FileName!);
        Assert.True(File.Exists(path), $"no dump at {path}");

        // Custom-format archives start with the magic "PGDMP". VerifyAfterWrite
        // already ran pg_restore --list over it, so reaching here means the
        // archive's table of contents parsed as well.
        var header = new byte[5];
        await using (var stream = File.OpenRead(path))
        {
            _ = await stream.ReadAsync(header);
        }

        Assert.Equal("PGDMP"u8.ToArray(), header);
    }

    /// <summary>
    /// The file name carries the hospital's time, and says so.
    ///
    /// It carried the UTC stamp with no zone, so a dump taken at 00:15 on the
    /// 20th in India was named for 18:45 on the 19th - beside a Backups page
    /// that showed the time as 00:15.
    /// </summary>
    [Fact]
    public async Task The_file_name_is_on_the_hospitals_clock_and_names_the_zone()
    {
        var options = new BackupOptions { Directory = NewDirectory() };

        var run = await CreateService(options).RunAsync(BackupTrigger.Manual);

        if (!ToolsUsable(options))
        {
            // Without pg_dump no file is written; the run records why instead.
            Assert.Equal(BackupStatus.Failed, run.Status);
            return;
        }

        var expectedStamp = run.StartedAtUtc.AddMinutes(330).ToString("yyyyMMdd-HHmmss");
        // Plain by default (encryption is a setting that is off), so the name ends in .dump.
        Assert.Equal($"hospitalpm-{expectedStamp}-IST.dump", run.FileName);
        Assert.False(new BackupOptions().Encrypt, "backups are plain unless encryption is switched on");
    }

    [Fact]
    public async Task The_run_is_recorded_in_the_database()
    {
        var options = new BackupOptions { Directory = NewDirectory() };

        var run = await CreateService(options).RunAsync(BackupTrigger.Manual);

        await using var db = fixture.CreateContext();
        var stored = await db.BackupRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);

        Assert.Equal(BackupTrigger.Manual, stored.Trigger);
        Assert.NotNull(stored.FinishedAtUtc);
        Assert.NotNull(stored.DurationMs);

        // Whatever the outcome, a finished run never stays Running — a row
        // stuck there is how a dead service would look, and the dashboard
        // reads this column to decide what to tell an administrator.
        Assert.NotEqual(BackupStatus.Running, stored.Status);
    }

    [Fact]
    public async Task A_failure_is_recorded_with_a_readable_reason()
    {
        var options = new BackupOptions
        {
            Directory = NewDirectory(),
            PgDumpPath = Path.Combine(Path.GetTempPath(), "definitely-not-pg-dump-here"),
        };

        var run = await CreateService(options).RunAsync(BackupTrigger.Scheduled);

        Assert.Equal(BackupStatus.Failed, run.Status);
        Assert.NotNull(run.Error);

        // The reader is a hospital IT contact, so the message names the thing
        // that is wrong rather than the exception type.
        Assert.Contains("pg_dump", run.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Exception", run.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retention_keeps_the_newest_and_leaves_other_files_alone()
    {
        var options = new BackupOptions { Directory = NewDirectory(), RetainCount = 2 };

        if (!ToolsUsable(options)) return;

        Directory.CreateDirectory(options.Directory);

        // Older dumps, and one file this app did not write.
        foreach (var stamp in new[] { "20200101-000000", "20200102-000000", "20200103-000000" })
        {
            await File.WriteAllTextAsync(
                Path.Combine(options.Directory, $"hospitalpm-{stamp}.dump"), "old");
        }

        var bystander = Path.Combine(options.Directory, "ward-inventory.xlsx");
        await File.WriteAllTextAsync(bystander, "not ours");

        var run = await CreateService(options).RunAsync(BackupTrigger.Scheduled);
        Assert.Equal(BackupStatus.Succeeded, run.Status);

        var remaining = Directory.GetFiles(options.Directory, "hospitalpm-*.dump*")
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(2, remaining.Count);
        Assert.Contains(run.FileName, remaining);

        // The oldest two go; a hospital pointing this at a shared drive must
        // not find its other files deleted.
        Assert.DoesNotContain("hospitalpm-20200101-000000.dump", remaining);
        Assert.True(File.Exists(bystander), "a file we did not write was deleted");
    }

    [Fact]
    public void An_older_pg_dump_than_the_server_is_refused_rather_than_attempted()
    {
        var locator = new PgToolLocator(Options.Create(new BackupOptions()));

        // Pretend the server is far newer than anything installed here.
        var tool = locator.FindPgDump(new Version(99, 0));

        Assert.False(tool.IsUsable);
        Assert.NotNull(tool.Problem);

        // pg_dump refuses a newer server anyway; catching it here is what puts
        // the reason somewhere an administrator will see it.
        Assert.Contains("99", tool.Problem!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_configured_path_that_does_not_exist_says_so()
    {
        var locator = new PgToolLocator(Options.Create(new BackupOptions
        {
            PgDumpPath = Path.Combine(Path.GetTempPath(), "nope", "pg_dump"),
        }));

        var tool = locator.FindPgDump(null);

        Assert.False(tool.IsUsable);
        Assert.Contains("configured", tool.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unusable_pg_dump_earlier_on_PATH_does_not_hide_a_working_one()
    {
        var options = new BackupOptions();
        if (!ToolsUsable(options)) return;

        // The bug this guards. CI runs on a machine with an older PostgreSQL
        // client and the pinned 18 both installed, where /usr/bin/pg_dump
        // resolves to the older one. The locator stopped at the first binary it
        // found and reported "too old", never reaching the working 18 in a
        // version-numbered directory. A hospital that upgraded its PostgreSQL
        // major version would have silently stopped backing up.
        var junkDir = NewDirectory();
        Directory.CreateDirectory(junkDir);

        var junkName = OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump";
        File.WriteAllText(Path.Combine(junkDir, junkName), "not a real executable");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable(
                "PATH", junkDir + Path.PathSeparator + originalPath);

            var tool = new PgToolLocator(Options.Create(options)).FindPgDump(new Version(18, 0));

            Assert.True(tool.IsUsable,
                $"the junk pg_dump masked the real one: {tool.Problem}");
            Assert.NotEqual(junkDir, Path.GetDirectoryName(tool.Path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public async Task A_run_left_running_is_closed_off_rather_than_hanging_for_ever()
    {
        await using var db = fixture.CreateContext();

        // Exactly what a restore leaves behind. A backup writes its row as
        // Running before pg_dump starts, so the dump captures that row
        // mid-flight - every restored database inherits one, and without
        // this it sits on the Backups page as a backup running for weeks.
        var stranded = new BackupRun
        {
            StartedAtUtc = DateTime.UtcNow.AddDays(-3),
            Status = BackupStatus.Running,
            Trigger = BackupTrigger.Scheduled,
        };
        db.BackupRuns.Add(stranded);
        await db.SaveChangesAsync();

        var closedCount = await Api.Hosting.InterruptedBackups.CloseAsync(
            db, NullLogger.Instance);

        Assert.True(closedCount >= 1);

        await using var reread = fixture.CreateContext();
        var closed = await reread.BackupRuns.AsNoTracking()
            .SingleAsync(r => r.Id == stranded.Id);

        Assert.Equal(BackupStatus.Failed, closed.Status);
        Assert.NotNull(closed.FinishedAtUtc);
        Assert.Contains("Interrupted", closed.Error!, StringComparison.Ordinal);
    }
}
