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
            configuration,
            wrapped,
            TimeProvider.System,
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
        var tool = locator.FindPgDump(new Version(17, 0));

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
    public async Task A_backup_is_written_and_can_be_read_back()
    {
        var options = new BackupOptions { Directory = NewDirectory() };

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

        var remaining = Directory.GetFiles(options.Directory, "hospitalpm-*.dump")
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
}
