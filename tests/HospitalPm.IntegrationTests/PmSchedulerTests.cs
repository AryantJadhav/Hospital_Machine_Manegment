using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HospitalPm.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public sealed class PmSchedulerTests(PostgresFixture fixture)
{
    private static PmScheduleGenerator Generator(HospitalPmDbContext db, DateTimeOffset now, int horizonDays = 60)
    {
        var options = Options.Create(new ScheduleOptions { HorizonDays = horizonDays });
        var clock = new HospitalClock(new FixedClock(now), options);
        return new PmScheduleGenerator(db, clock, options);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // ---------------- generation ----------------

    [Fact]
    public async Task Generation_creates_occurrences_up_to_the_horizon()
    {
        await using var db = fixture.CreateContext();
        var schedule = await NewScheduleAsync(db, PmFrequency.Monthly, anchor: new DateOnly(2026, 1, 1));

        await Generator(db, new DateTimeOffset(2026, 1, 1, 6, 0, 0, TimeSpan.Zero), horizonDays: 90).RunAsync();

        var dues = await db.PmTasks.AsNoTracking()
            .Where(t => t.PmScheduleId == schedule.Id)
            .Select(t => t.DueDate)
            .OrderBy(d => d)
            .ToListAsync();

        Assert.Equal(
            [new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 1)],
            dues);
    }

    [Fact]
    public async Task Running_generation_twice_creates_nothing_the_second_time()
    {
        await using var db = fixture.CreateContext();
        var schedule = await NewScheduleAsync(db, PmFrequency.Monthly, anchor: new DateOnly(2026, 1, 1));
        var now = new DateTimeOffset(2026, 1, 1, 6, 0, 0, TimeSpan.Zero);

        var first = await Generator(db, now).RunAsync();
        var second = await Generator(db, now).RunAsync();

        // The job runs nightly, gets triggered by hand, and reruns after a
        // restart. None of that may double a technician's workload.
        Assert.True(first.Created > 0);
        Assert.Equal(0, second.Created);
    }

    [Fact]
    public async Task Generation_backfills_a_schedule_anchored_in_the_past()
    {
        await using var db = fixture.CreateContext();
        var schedule = await NewScheduleAsync(db, PmFrequency.Quarterly, anchor: new DateOnly(2025, 1, 1));

        await Generator(db, new DateTimeOffset(2026, 2, 1, 6, 0, 0, TimeSpan.Zero)).RunAsync();

        var overdue = await db.PmTasks.AsNoTracking()
            .CountAsync(t => t.PmScheduleId == schedule.Id && t.Status == PmTaskStatus.Overdue);

        // A machine in service for a year before anyone set up its schedule
        // has a real compliance gap, and an auditor expects to see it rather
        // than a register that starts from today.
        Assert.True(overdue >= 4, $"expected the missed year to appear as overdue, found {overdue}");
    }

    [Fact]
    public async Task Statuses_advance_as_dates_pass()
    {
        await using var db = fixture.CreateContext();
        var schedule = await NewScheduleAsync(db, PmFrequency.Monthly, anchor: new DateOnly(2026, 3, 1), graceDays: 3);

        // Generate while everything is still in the future.
        await Generator(db, new DateTimeOffset(2026, 2, 20, 6, 0, 0, TimeSpan.Zero)).RunAsync();
        Assert.Equal(PmTaskStatus.Scheduled, await StatusOfAsync(db, schedule.Id, new DateOnly(2026, 3, 1)));

        // On the due date, inside grace.
        await Generator(db, new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero)).RunAsync();
        Assert.Equal(PmTaskStatus.Due, await StatusOfAsync(db, schedule.Id, new DateOnly(2026, 3, 1)));

        // Last day of grace.
        await Generator(db, new DateTimeOffset(2026, 3, 4, 6, 0, 0, TimeSpan.Zero)).RunAsync();
        Assert.Equal(PmTaskStatus.Due, await StatusOfAsync(db, schedule.Id, new DateOnly(2026, 3, 1)));

        // Grace exhausted.
        await Generator(db, new DateTimeOffset(2026, 3, 5, 6, 0, 0, TimeSpan.Zero)).RunAsync();
        Assert.Equal(PmTaskStatus.Overdue, await StatusOfAsync(db, schedule.Id, new DateOnly(2026, 3, 1)));
    }

    [Fact]
    public async Task A_completed_task_never_becomes_overdue()
    {
        await using var db = fixture.CreateContext();
        var schedule = await NewScheduleAsync(db, PmFrequency.Monthly, anchor: new DateOnly(2026, 3, 1));

        await Generator(db, new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero)).RunAsync();

        var task = await db.PmTasks.FirstAsync(t => t.PmScheduleId == schedule.Id);
        await CompleteAsync(db, task);

        // Months later the generator runs again. A PM done on time must not
        // be rewritten as a miss.
        await Generator(db, new DateTimeOffset(2026, 9, 1, 6, 0, 0, TimeSpan.Zero)).RunAsync();

        var reloaded = await db.PmTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        Assert.Equal(PmTaskStatus.Completed, reloaded.Status);
    }

    // ---------------- completion is evidence ----------------

    [Fact]
    public async Task A_completed_task_cannot_be_reopened()
    {
        await using var db = fixture.CreateContext();
        var task = await CompletedTaskAsync(db);

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE pm_task SET status = 20 WHERE id = {0}", task.Id));

        Assert.Contains("cannot be changed", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_completed_task_cannot_have_its_signature_swapped()
    {
        await using var db = fixture.CreateContext();
        var task = await CompletedTaskAsync(db);

        // "Who signed off this PM" has to be a fact, not a field.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE pm_task SET completed_by_user_id = 99999 WHERE id = {0}", task.Id));

        Assert.Contains("fixed", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_completed_task_cannot_be_deleted()
    {
        await using var db = fixture.CreateContext();
        var task = await CompletedTaskAsync(db);

        // Erasing a PM from the record is worse than editing it.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM pm_task WHERE id = {0}", task.Id));

        Assert.Contains("cannot be deleted", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("completed_by_user_id = 1")]
    [InlineData("completed_at_utc = now()")]
    public async Task A_completion_missing_when_or_by_whom_is_refused(string only)
    {
        await using var db = fixture.CreateContext();
        var schedule = await NewScheduleAsync(db, PmFrequency.Monthly, anchor: new DateOnly(2026, 3, 1));
        await Generator(db, new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero)).RunAsync();
        var task = await db.PmTasks.AsNoTracking().FirstAsync(t => t.PmScheduleId == schedule.Id);

        // A completed PM has to say when it was done and by whom, or it is not a record. It no
        // longer has to name a checklist version: a PM may have had no checklist at all.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE pm_task SET status = 40, " + only + " WHERE id = {0}",
                task.Id));

        Assert.Equal("23514", ex.SqlState); // check_violation
    }

    [Fact]
    public async Task A_skip_without_a_reason_is_refused()
    {
        await using var db = fixture.CreateContext();
        var schedule = await NewScheduleAsync(db, PmFrequency.Monthly, anchor: new DateOnly(2026, 3, 1));
        await Generator(db, new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero)).RunAsync();
        var task = await db.PmTasks.AsNoTracking().FirstAsync(t => t.PmScheduleId == schedule.Id);

        // "Not done" with no explanation is the most useless row a compliance
        // report can contain.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE pm_task SET status = 50 WHERE id = {0}", task.Id));

        Assert.Equal("23514", ex.SqlState);
    }

    // ---------------- schedule integrity ----------------

    [Fact]
    public async Task A_named_frequency_cannot_carry_a_stray_interval()
    {
        await using var db = fixture.CreateContext();
        var equipment = await NewEquipmentAsync(db);
        var template = await NewTemplateAsync(db);

        db.PmSchedules.Add(new PmSchedule
        {
            EquipmentId = equipment.Id,
            ChecklistTemplateId = template.Id,
            Frequency = PmFrequency.Quarterly,
            IntervalDays = 45, // meaningless here, and silently ignored if allowed
            AnchorDate = new DateOnly(2026, 1, 1),
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_custom_frequency_requires_an_interval()
    {
        await using var db = fixture.CreateContext();
        var equipment = await NewEquipmentAsync(db);
        var template = await NewTemplateAsync(db);

        db.PmSchedules.Add(new PmSchedule
        {
            EquipmentId = equipment.Id,
            ChecklistTemplateId = template.Id,
            Frequency = PmFrequency.Custom,
            IntervalDays = 0,
            AnchorDate = new DateOnly(2026, 1, 1),
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task One_schedule_per_checklist_per_machine()
    {
        await using var db = fixture.CreateContext();
        var equipment = await NewEquipmentAsync(db);
        var template = await NewTemplateAsync(db);

        for (var i = 0; i < 2; i++)
        {
            db.PmSchedules.Add(new PmSchedule
            {
                EquipmentId = equipment.Id,
                ChecklistTemplateId = template.Id,
                Frequency = PmFrequency.Quarterly,
                AnchorDate = new DateOnly(2026, 1, 1),
            });

            if (i == 0)
            {
                await db.SaveChangesAsync();
            }
        }

        // Two schedules would generate duplicate work and double-count in
        // compliance reporting.
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Scheduling_is_audited()
    {
        await using var db = fixture.CreateContext();
        var schedule = await NewScheduleAsync(db, PmFrequency.Quarterly, anchor: new DateOnly(2026, 1, 1));

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE table_name = 'pm_schedule' AND record_pk = @pk", conn);
        cmd.Parameters.AddWithValue("pk", schedule.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(
            Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    // ---------------- helpers ----------------

    private static async Task<PmTaskStatus> StatusOfAsync(HospitalPmDbContext db, int scheduleId, DateOnly due)
        => await db.PmTasks.AsNoTracking()
            .Where(t => t.PmScheduleId == scheduleId && t.DueDate == due)
            .Select(t => t.Status)
            .SingleAsync();

    private static async Task<Domain.Assets.Equipment> NewEquipmentAsync(HospitalPmDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var room = new Location { Code = $"PMR-{suffix}", Name = $"Room {suffix}", Level = LocationLevel.Room };
        db.Locations.Add(room);
        await db.SaveChangesAsync();

        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");

        var equipment = new Domain.Assets.Equipment
        {
            AssetTag = $"PM-{suffix}".ToUpperInvariant(),
            EquipmentTypeId = type.Id,
            LocationId = room.Id,
        };
        db.Equipment.Add(equipment);
        await db.SaveChangesAsync();

        return equipment;
    }

    private static async Task<ChecklistTemplate> NewTemplateAsync(HospitalPmDbContext db)
    {
        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");

        var template = new ChecklistTemplate
        {
            EquipmentTypeId = type.Id,
            Code = "PMCL-" + Guid.NewGuid().ToString("N")[..10],
            Name = "Quarterly PM",
        };
        db.ChecklistTemplates.Add(template);
        await db.SaveChangesAsync();

        return template;
    }

    private static async Task<PmSchedule> NewScheduleAsync(
        HospitalPmDbContext db, PmFrequency frequency, DateOnly anchor, int graceDays = 0)
    {
        var equipment = await NewEquipmentAsync(db);
        var template = await NewTemplateAsync(db);

        var schedule = new PmSchedule
        {
            EquipmentId = equipment.Id,
            ChecklistTemplateId = template.Id,
            Frequency = frequency,
            AnchorDate = anchor,
            GraceDays = graceDays,
        };

        db.PmSchedules.Add(schedule);
        await db.SaveChangesAsync();

        return schedule;
    }

    private static async Task CompleteAsync(HospitalPmDbContext db, PmTask task)
    {
        // Fetched in two steps: EF cannot translate a subquery over another
        // DbSet used inside a predicate like this.
        var templateId = await db.PmSchedules
            .Where(s => s.Id == task.PmScheduleId)
            .Select(s => s.ChecklistTemplateId)
            .SingleAsync();

        var version = new ChecklistTemplateVersion
        {
            ChecklistTemplateId = templateId!.Value,
            VersionNo = 1,
            Status = ChecklistVersionStatus.Published,
            Definition = new ChecklistDefinition
            {
                Sections =
                [
                    new ChecklistSection
                    {
                        Title = "Checks",
                        Items = [new ChecklistItem { Key = "casing", Label = "Casing intact" }],
                    },
                ],
            },
            PublishedAtUtc = DateTime.UtcNow,
        };
        db.ChecklistTemplateVersions.Add(version);
        await db.SaveChangesAsync();

        // Created rather than looked up: test classes in this collection run
        // in no guaranteed order, so assuming another test has already made a
        // user is a race.
        var user = new Infrastructure.Identity.ApplicationUser
        {
            UserName = "pm-" + Guid.NewGuid().ToString("N")[..8],
            FullName = "PM Test Engineer",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        task.Status = PmTaskStatus.Completed;
        task.CompletedAtUtc = DateTime.UtcNow;
        task.CompletedByUserId = user.Id;
        task.ChecklistTemplateVersionId = version.Id;
        await db.SaveChangesAsync();
    }

    // ---------------- retired machines ----------------

    /// <summary>
    /// A condemned machine stops generating work.
    ///
    /// It stays on the register so its certificates remain readable, and is
    /// never maintained again. Before this, its schedule kept producing PM
    /// tasks forever — work nobody can do, on a machine that may no longer
    /// physically exist, in the list a technician reads every morning.
    ///
    /// Latent while schedules were created one at a time. Certain the moment
    /// a hospital can schedule two thousand at once.
    /// </summary>
    [Theory]
    [InlineData(Domain.Assets.EquipmentStatus.Condemned)]
    [InlineData(Domain.Assets.EquipmentStatus.Disposed)]
    public async Task A_retired_machine_generates_no_further_work(
        Domain.Assets.EquipmentStatus status)
    {
        await using var db = fixture.CreateContext();

        var schedule = await NewScheduleAsync(db, PmFrequency.Monthly, new DateOnly(2026, 1, 1));

        var equipment = await db.Equipment.SingleAsync(e => e.Id == schedule.EquipmentId);
        equipment.Status = status;
        await db.SaveChangesAsync();

        await Generator(db, new DateTimeOffset(2026, 6, 1, 6, 0, 0, TimeSpan.Zero)).RunAsync();

        // Scoped to THIS schedule, deliberately. The suite shares one
        // database, so GenerationResult.Created counts every schedule any
        // other test left behind — asserting on it passed alone and failed in
        // a full run, which is a test measuring the wrong thing rather than a
        // product that behaves differently.
        Assert.Equal(0, await db.PmTasks.CountAsync(t => t.PmScheduleId == schedule.Id));
    }

    /// <summary>
    /// The counterpart, so the test above cannot pass by generating nothing
    /// at all. A machine out for repair is still owned, still coming back,
    /// and still due its PM.
    /// </summary>
    [Fact]
    public async Task A_machine_under_repair_is_still_maintained()
    {
        await using var db = fixture.CreateContext();

        var schedule = await NewScheduleAsync(db, PmFrequency.Monthly, new DateOnly(2026, 1, 1));

        var equipment = await db.Equipment.SingleAsync(e => e.Id == schedule.EquipmentId);
        equipment.Status = Domain.Assets.EquipmentStatus.UnderRepair;
        await db.SaveChangesAsync();

        await Generator(db, new DateTimeOffset(2026, 6, 1, 6, 0, 0, TimeSpan.Zero)).RunAsync();

        Assert.True(await db.PmTasks.CountAsync(t => t.PmScheduleId == schedule.Id) > 0);
    }

    private static async Task<PmTask> CompletedTaskAsync(HospitalPmDbContext db)
    {
        var schedule = await NewScheduleAsync(db, PmFrequency.Monthly, new DateOnly(2026, 3, 1));

        var options = Options.Create(new ScheduleOptions());
        var clock = new HospitalClock(new FixedClock(new DateTimeOffset(2026, 3, 1, 6, 0, 0, TimeSpan.Zero)), options);
        await new PmScheduleGenerator(db, clock, options).RunAsync();

        var task = await db.PmTasks.FirstAsync(t => t.PmScheduleId == schedule.Id);
        await CompleteAsync(db, task);

        return task;
    }
}
