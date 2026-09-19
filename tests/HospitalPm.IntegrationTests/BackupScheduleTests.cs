using Hangfire;
using Hangfire.Storage;
using HospitalPm.Domain.Operations;
using HospitalPm.Infrastructure.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The nightly jobs, as Hangfire runs them.
///
/// BackupTests call the service directly, which proves a backup can be taken
/// and says nothing about whether anything ever asks for one. These boot the
/// real host, whose start-up registers the recurring jobs and starts the
/// in-process Hangfire server, and check both halves: that the jobs are
/// registered the way a hospital would get them, and that firing one produces
/// a backup run.
///
/// The times are asserted on the hospital's clock rather than as cron text.
/// Both jobs were written in UTC and read as though they were local, so the
/// backup ran at 08:00 and generation at 05:45 India time; a test comparing
/// cron strings would have passed throughout.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class BackupScheduleTests(PostgresFixture fixture) : IDisposable
{
    private const string JobId = "nightly-backup";
    private const string GenerateJobId = "pm-generate-due-dates";

    private readonly ApiFactory _factory = new(fixture.ConnectionString);

    public void Dispose() => _factory.Dispose();

    [Fact]
    public void The_job_is_registered_daily_in_utc()
    {
        // Touching Services builds and starts the host, which registers jobs.
        using var connection = _factory.Services.GetRequiredService<JobStorage>().GetConnection();

        var job = connection.GetRecurringJobs().SingleOrDefault(j => j.Id == JobId);

        Assert.NotNull(job);
        // 21:00 UTC is 02:30 in India — the middle of the hospital's night.
        // At 02:30 UTC it ran at 08:00 India time, as the day shift arrived.
        Assert.Equal("0 21 * * *", job.Cron);
        Assert.Equal("UTC", job.TimeZoneId);

        // Asserted as the time it will actually fire on the hospital's clock,
        // not just as the cron text: the whole point of the change was the hour
        // a technician experiences, and an arithmetic slip in a cron string is
        // exactly the mistake a matching-string test cannot catch.
        Assert.NotNull(job.NextExecution);
        var hospital = _factory.Services.GetRequiredService<HospitalClock>();
        var fires = job.NextExecution!.Value + hospital.Offset;

        Assert.Equal(2, fires.Hour);
        Assert.Equal(30, fires.Minute);
    }

    /// <summary>
    /// PM generation runs a quarter of an hour into the hospital's day, and
    /// before that night's backup, so the dump holds the tasks just generated.
    /// </summary>
    [Fact]
    public void Generation_runs_at_a_quarter_past_midnight_and_before_the_backup()
    {
        using var connection = _factory.Services.GetRequiredService<JobStorage>().GetConnection();
        var jobs = connection.GetRecurringJobs();

        var generate = jobs.SingleOrDefault(j => j.Id == GenerateJobId);
        var backup = jobs.SingleOrDefault(j => j.Id == JobId);

        Assert.NotNull(generate);
        Assert.NotNull(backup);
        Assert.NotNull(generate.NextExecution);
        Assert.NotNull(backup.NextExecution);

        var offset = _factory.Services.GetRequiredService<HospitalClock>().Offset;
        var generateFires = generate.NextExecution!.Value + offset;

        Assert.Equal(0, generateFires.Hour);
        Assert.Equal(15, generateFires.Minute);

        // And in that order within one night. Compared as minutes past the
        // hospital's midnight, because the two next-executions can fall on
        // different calendar days depending on when the suite happens to run.
        var backupFires = backup.NextExecution!.Value + offset;
        var generateMinutes = (generateFires.Hour * 60) + generateFires.Minute;
        var backupMinutes = (backupFires.Hour * 60) + backupFires.Minute;

        Assert.True(
            generateMinutes < backupMinutes,
            $"generation fires at {generateFires:HH:mm} and the backup at {backupFires:HH:mm}; "
            + "the backup must follow generation so a dump holds that day's tasks");
    }

    /// <summary>
    /// Fires the job and waits for the run it should leave behind.
    ///
    /// Asserted on the run existing and being finished, not on it succeeding:
    /// whether pg_dump is present is BackupTests' concern, and a failed run
    /// still proves the schedule reached the service. What would fail here is
    /// the job never being picked up at all.
    /// </summary>
    [Fact]
    public async Task Firing_the_job_takes_a_scheduled_backup()
    {
        var startedAfter = DateTime.UtcNow.AddSeconds(-1);
        _ = _factory.Services;

        _factory.Services.GetRequiredService<IRecurringJobManager>().Trigger(JobId);

        // Hangfire polls its queue on an interval of its own, so this is a
        // deadline and not a sleep: it returns as soon as the run appears.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        BackupRun? run = null;

        while (DateTime.UtcNow < deadline)
        {
            await using var db = fixture.CreateContext();
            run = await db.BackupRuns.AsNoTracking()
                .Where(r => r.Trigger == BackupTrigger.Scheduled
                            && r.StartedAtUtc >= startedAfter
                            && r.Status != BackupStatus.Running)
                .OrderByDescending(r => r.StartedAtUtc)
                .FirstOrDefaultAsync();

            if (run is not null) break;
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        Assert.True(run is not null, "the job was triggered but no scheduled backup run appeared within 90 seconds");
    }
}
