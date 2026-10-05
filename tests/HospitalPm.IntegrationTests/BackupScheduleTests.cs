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
        // Every two hours from midnight on the hospital's clock. In India (UTC+5:30) that is :30 past every even UTC hour.
        // At 02:30 UTC it once ran at 08:00 India time, as the day shift arrived.
        Assert.Equal("30 0,2,4,6,8,10,12,14,16,18,20,22 * * *", job.Cron);
        Assert.Equal("UTC", job.TimeZoneId);

        // Asserted as the time it will actually fire on the hospital's clock,
        // not just as the cron text: the whole point of the change was the hour
        // a technician experiences, and an arithmetic slip in a cron string is
        // exactly the mistake a matching-string test cannot catch.
        Assert.NotNull(job.NextExecution);
        var hospital = _factory.Services.GetRequiredService<HospitalClock>();
        var fires = job.NextExecution!.Value + hospital.Offset;

        // On the hour, at an even hour of the hospital's day: 00:00, 02:00, 04:00 and so on.
        Assert.Equal(0, fires.Minute);
        Assert.Equal(0, fires.Hour % 2);
    }

    [Theory]
    [InlineData("00:00", 330, 2, "30 0,2,4,6,8,10,12,14,16,18,20,22 * * *")] // India, every two hours from midnight: the default
    [InlineData("00:00", 0, 2, "0 0,2,4,6,8,10,12,14,16,18,20,22 * * *")]
    [InlineData("00:00", 60, 2, "0 1,3,5,7,9,11,13,15,17,19,21,23 * * *")] // midnight at UTC+1 is 23:00 UTC: odd hours
    [InlineData("03:00", 330, 24, "30 21 * * *")] // once a day at three in the morning, as before
    [InlineData("3:00", 330, 24, "30 21 * * *")]
    [InlineData("03:00", 0, 12, "0 3,15 * * *")]
    [InlineData("01:00", 60, 6, "0 0,6,12,18 * * *")]
    [InlineData("23:30", -300, 24, "30 4 * * *")] // west of UTC, past midnight UTC
    [InlineData("00:00", 330, 1, "30 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23 * * *")]
    public void The_start_and_the_interval_become_a_utc_cron_for_the_hospitals_own_offset(string at, int offsetMinutes, int every, string cron)
    {
        var options = new Infrastructure.Operations.BackupOptions { StartsAt = at, EveryHours = every };

        Assert.Equal(cron, Infrastructure.Operations.BackupOptions.CronFor(options.StartsAtLocal(), TimeSpan.FromMinutes(offsetMinutes), options.EveryHoursChecked()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("25:00")]
    [InlineData("-1:00")]
    [InlineData("3")]
    public void A_time_that_cannot_be_read_falls_back_to_midnight_and_never_switches_the_backup_off(string at)
    {
        var options = new Infrastructure.Operations.BackupOptions { StartsAt = at };

        Assert.Equal(TimeSpan.Zero, options.StartsAtLocal());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(6, 6)]
    [InlineData(8, 8)]
    [InlineData(12, 12)]
    [InlineData(24, 24)]
    [InlineData(5, 2)]
    [InlineData(0, 2)]
    [InlineData(-2, 2)]
    [InlineData(48, 2)]
    public void Only_an_interval_that_divides_a_day_is_used_and_anything_else_means_every_two_hours(int configured, int used)
    {
        Assert.Equal(used, new Infrastructure.Operations.BackupOptions { EveryHours = configured }.EveryHoursChecked());
        Assert.Equal(2, new Infrastructure.Operations.BackupOptions().EveryHours);
    }

    [Fact]
    public void Two_hourly_backups_are_kept_for_a_fortnight_here_and_on_the_drive()
    {
        var options = new Infrastructure.Operations.BackupOptions();

        Assert.Equal(14 * 12, options.RetainCount);
        Assert.Equal(14 * 12, options.Drive.KeepCount);
    }

    /// <summary>
    /// PM generation runs a quarter of an hour into the hospital's day. The backup is every two hours, so the 02:00 one is
    /// the first to hold the tasks just generated, and the one at 00:00 holds the day before's.
    /// </summary>
    [Fact]
    public void Generation_runs_at_a_quarter_past_midnight_and_the_backup_follows_it_within_two_hours()
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

        // Whichever backup follows the generation does so within two hours: it fires on an even hour, and the generation is
        // a quarter past midnight, so the next one is at 02:00.
        var backupFires = backup.NextExecution!.Value + offset;
        Assert.Equal(0, backupFires.Minute);
        Assert.Equal(0, backupFires.Hour % 2);
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
