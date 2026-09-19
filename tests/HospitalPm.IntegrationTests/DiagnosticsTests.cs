using Microsoft.EntityFrameworkCore;
using HospitalPm.Domain.Operations;
using HospitalPm.Infrastructure.Operations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Diagnostics, against the real database.
///
/// The checks worth testing are the unhappy ones. A page that says everything
/// is fine when everything is fine is easy; the value is in what it says at
/// three in the afternoon when backups have not run for a week, and whether
/// the words point at something a hospital's IT contact can actually do.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DiagnosticsTests(PostgresFixture fixture)
{
    private DiagnosticsService CreateService(BackupOptions? options = null)
    {
        options ??= new BackupOptions();
        var wrapped = Options.Create(options);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HospitalPm"] = fixture.ConnectionString,
            })
            .Build();

        var db = fixture.CreateContext();
        var locator = new PgToolLocator(wrapped);
        var hospital = new HospitalPm.Infrastructure.Maintenance.HospitalClock(
            TimeProvider.System,
            Options.Create(new HospitalPm.Infrastructure.Maintenance.ScheduleOptions()));

        return new DiagnosticsService(
            db,
            new BackupService(db, locator, configuration, wrapped, TimeProvider.System, hospital,
                NullLogger<BackupService>.Instance),
            locator,
            new HospitalPm.Infrastructure.Licensing.LicenceService(
                Options.Create(new HospitalPm.Infrastructure.Licensing.LicenceOptions()),
                TimeProvider.System),
            wrapped,
            TimeProvider.System,
            hospital);
    }

    private static Check Find(Diagnostics d, string name) =>
        d.Checks.Single(c => c.Name == name);

    [Fact]
    public async Task The_database_and_schema_report_healthy_against_a_migrated_database()
    {
        var result = await CreateService().RunAsync([]);

        var database = Find(result, "Database");
        Assert.True(database.State == CheckState.Ok,
            $"database check was {database.State}: {database.Detail}");
        Assert.Contains("PostgreSQL 17", database.Detail, StringComparison.Ordinal);

        // Migrations run on start; a pending one means the service is serving
        // an older schema than the code expects.
        Assert.Equal(CheckState.Ok, Find(result, "Schema").State);
    }

    [Fact]
    public async Task The_overall_state_is_the_worst_single_check()
    {
        // pg_dump deliberately missing, which is a Problem on its own.
        var result = await CreateService(new BackupOptions
        {
            PgDumpPath = Path.Combine(Path.GetTempPath(), "no-pg-dump-here"),
        }).RunAsync([]);

        Assert.Equal(CheckState.Problem, Find(result, "Backup tool").State);

        // One red row has to make the whole page red, or an administrator
        // reads the headline and stops.
        Assert.Equal(CheckState.Problem, result.Overall);
        Assert.Equal(result.Checks.Max(c => c.State), result.Overall);
    }

    [Fact]
    public async Task A_missing_backup_tool_says_what_to_do_about_it()
    {
        var result = await CreateService(new BackupOptions
        {
            PgDumpPath = Path.Combine(Path.GetTempPath(), "no-pg-dump-here"),
        }).RunAsync([]);

        var check = Find(result, "Backup tool");

        Assert.NotNull(check.Advice);
        Assert.Contains("Backups cannot run", check.Advice!, StringComparison.Ordinal);

        // The reader is a hospital IT contact, not an engineer.
        Assert.DoesNotContain("Exception", check.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("null", check.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Never_having_backed_up_is_a_problem_not_a_blank()
    {
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("delete from backup_run;");
        }

        var result = await CreateService().RunAsync([]);
        var check = Find(result, "Backups");

        // Silence is the failure mode this whole area exists to prevent.
        Assert.Equal(CheckState.Problem, check.State);
        Assert.Contains("has ever run", check.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(check.Advice);
    }

    [Fact]
    public async Task A_stale_backup_is_a_problem_even_though_one_once_succeeded()
    {
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("delete from backup_run;");

            db.BackupRuns.Add(new BackupRun
            {
                StartedAtUtc = DateTime.UtcNow.AddDays(-9),
                FinishedAtUtc = DateTime.UtcNow.AddDays(-9),
                Status = BackupStatus.Succeeded,
                Trigger = BackupTrigger.Scheduled,
                FileName = "hospitalpm-old.dump",
                SizeBytes = 1234,
            });
            await db.SaveChangesAsync();
        }

        var result = await CreateService().RunAsync([]);
        var check = Find(result, "Backups");

        Assert.Equal(CheckState.Problem, check.State);
        Assert.Contains("9 days", check.Detail, StringComparison.Ordinal);
        Assert.Contains("not running", check.Advice!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_recent_success_followed_by_a_failure_warns_without_crying_wolf()
    {
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("delete from backup_run;");

            db.BackupRuns.Add(new BackupRun
            {
                StartedAtUtc = DateTime.UtcNow.AddHours(-6),
                FinishedAtUtc = DateTime.UtcNow.AddHours(-6),
                Status = BackupStatus.Succeeded,
                Trigger = BackupTrigger.Scheduled,
                FileName = "hospitalpm-good.dump",
                SizeBytes = 4096,
            });
            db.BackupRuns.Add(new BackupRun
            {
                StartedAtUtc = DateTime.UtcNow.AddHours(-1),
                FinishedAtUtc = DateTime.UtcNow.AddHours(-1),
                Status = BackupStatus.Failed,
                Trigger = BackupTrigger.Manual,
                Error = "The backup drive is out of space.",
            });
            await db.SaveChangesAsync();
        }

        var result = await CreateService().RunAsync([]);
        var check = Find(result, "Backups");

        // There is still a usable backup from six hours ago, so this is not a
        // red alert — but tonight's run will fail the same way.
        Assert.Equal(CheckState.Warning, check.State);
        Assert.Contains("out of space", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disk_space_and_clock_are_reported_for_the_backup_drive()
    {
        var result = await CreateService().RunAsync([]);

        var disk = Find(result, "Disk space");
        Assert.NotEqual(CheckState.Problem, disk.State);
        Assert.Contains("free", disk.Detail, StringComparison.OrdinalIgnoreCase);

        // PM due dates are dates, not instants, so a drifted clock marks work
        // overdue on the wrong day. The time is the hospital's, named as such.
        Assert.Contains("IST", Find(result, "Clock").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_check_reads_as_something_a_non_engineer_could_act_on()
    {
        var result = await CreateService().RunAsync([]);

        Assert.NotEmpty(result.Checks);

        foreach (var check in result.Checks)
        {
            Assert.False(string.IsNullOrWhiteSpace(check.Name));
            Assert.False(string.IsNullOrWhiteSpace(check.Detail));

            // Anything not OK has to say what to do next; a red row with no
            // advice is just an alarm.
            if (check.State != CheckState.Ok)
            {
                Assert.False(string.IsNullOrWhiteSpace(check.Advice),
                    $"'{check.Name}' is {check.State} but offers no advice");
            }

            Assert.DoesNotContain("Npgsql", check.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("System.", check.Detail, StringComparison.Ordinal);
        }
    }
}
