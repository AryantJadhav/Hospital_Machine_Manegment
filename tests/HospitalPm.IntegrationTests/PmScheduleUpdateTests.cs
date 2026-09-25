using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Changing how a machine's PM runs after it has been set up.
///
/// The rule that matters is what is left alone. The PMs generated ahead are remade
/// under the new pattern, because otherwise a machine moved from quarterly to monthly
/// carries both sets of dates. Anything that has happened, or is owed now, is kept:
/// a completed PM is evidence, and an overdue one is work somebody still has to do.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PmScheduleUpdateTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "PmUpdate2026!";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _suffix = null!;
    private int _machineId;
    private int _scheduleId;

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _client = _factory.CreateClient();
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var room = new Location { Code = $"PU-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();

            var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
            var machine = new Domain.Assets.Equipment
            {
                AssetTag = $"PU-{_suffix}", EquipmentTypeId = type.Id, LocationId = room.Id,
            };
            var template = new ChecklistTemplate { EquipmentTypeId = type.Id, Code = $"pu-{_suffix}", Name = "Update PM" };
            db.AddRange(machine, template);
            await db.SaveChangesAsync();
            _machineId = machine.Id;

            // Monthly from ten days out: two tasks fall inside the sixty-day window.
            var schedule = new PmSchedule
            {
                EquipmentId = machine.Id, ChecklistTemplateId = template.Id,
                Frequency = PmFrequency.Monthly, AnchorDate = Today().AddDays(10), GraceDays = 7,
            };
            db.PmSchedules.Add(schedule);
            await db.SaveChangesAsync();
            _scheduleId = schedule.Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = $"pu-{_suffix}", FullName = "PM Update Admin", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, Domain.Identity.Roles.Admin);
        }

        var login = await _client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"pu-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        // The tasks the schedule would have generated.
        (await _client.PostAsync("/api/pm/generate", null)).EnsureSuccessStatusCode();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private object Update(
        PmFrequency frequency, DateOnly next, int grace = 7, bool active = true, int interval = 0) => new
        {
            frequency = (int)frequency,
            intervalDays = interval,
            nextDueDate = next.ToString("yyyy-MM-dd"),
            graceDays = grace,
            isActive = active,
        };

    private async Task<List<PmTask>> TasksAsync()
    {
        await using var db = fixture.CreateContext();
        return await db.PmTasks.AsNoTracking()
            .Where(t => t.PmScheduleId == _scheduleId).OrderBy(t => t.DueDate).ToListAsync();
    }

    [Fact]
    public async Task The_schedule_starts_with_the_tasks_it_generated()
    {
        var tasks = await TasksAsync();

        Assert.Equal(2, tasks.Count);
        Assert.Equal(Today().AddDays(10), tasks[0].DueDate);
    }

    [Fact]
    public async Task Changing_the_frequency_remakes_the_dates_ahead_under_the_new_pattern()
    {
        var before = (await TasksAsync()).Select(t => t.Id).ToHashSet();
        var next = Today().AddDays(20);

        var res = await _client.PutAsJsonAsync(
            $"/api/pm/schedules/{_scheduleId}", Update(PmFrequency.Quarterly, next));

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var after = await TasksAsync();

        // Quarterly from twenty days out: only the first falls in the window. Neither of
        // the old monthly tasks is still there.
        Assert.Single(after);
        Assert.Equal(next, after[0].DueDate);
        Assert.DoesNotContain(after[0].Id, before);

        await using var db = fixture.CreateContext();
        var schedule = await db.PmSchedules.SingleAsync(s => s.Id == _scheduleId);
        Assert.Equal(PmFrequency.Quarterly, schedule.Frequency);
        Assert.Equal(next, schedule.AnchorDate);
    }

    [Fact]
    public async Task Changing_nothing_but_the_grace_leaves_every_task_as_it_was()
    {
        var before = (await TasksAsync()).Select(t => t.Id).ToList();

        var res = await _client.PutAsJsonAsync(
            $"/api/pm/schedules/{_scheduleId}", Update(PmFrequency.Monthly, Today().AddDays(10), grace: 21));

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal(before, (await TasksAsync()).Select(t => t.Id).ToList());

        await using var db = fixture.CreateContext();
        Assert.Equal(21, (await db.PmSchedules.SingleAsync(s => s.Id == _scheduleId)).GraceDays);
    }

    [Fact]
    public async Task Work_that_is_owed_or_closed_survives_a_change_of_pattern()
    {
        await using (var db = fixture.CreateContext())
        {
            var signer = await db.Users.FirstAsync(u => u.UserName == $"pu-{_suffix}");
            db.PmTasks.AddRange(
                new PmTask
                {
                    PmScheduleId = _scheduleId, EquipmentId = _machineId,
                    DueDate = Today().AddDays(-40), Status = PmTaskStatus.Overdue,
                },
                // Closed with a reason. A skipped PM is a record the database will not let
                // anyone delete or reopen, as a completed one is, and is far simpler to build.
                new PmTask
                {
                    PmScheduleId = _scheduleId, EquipmentId = _machineId,
                    DueDate = Today().AddDays(-70), Status = PmTaskStatus.Skipped,
                    SkipReason = "Machine was away for repair", CompletedAtUtc = DateTime.UtcNow.AddDays(-68),
                    CompletedByUserId = signer.Id,
                });
            await db.SaveChangesAsync();
        }

        var res = await _client.PutAsJsonAsync(
            $"/api/pm/schedules/{_scheduleId}", Update(PmFrequency.HalfYearly, Today().AddDays(30)));

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var after = await TasksAsync();
        Assert.Contains(after, t => t.Status == PmTaskStatus.Overdue && t.DueDate == Today().AddDays(-40));
        Assert.Contains(after, t => t.Status == PmTaskStatus.Skipped && t.DueDate == Today().AddDays(-70));
        Assert.Contains(after, t => t.DueDate == Today().AddDays(30));
    }

    [Fact]
    public async Task Stopping_a_schedule_removes_what_is_ahead_and_generates_no_more()
    {
        var res = await _client.PutAsJsonAsync(
            $"/api/pm/schedules/{_scheduleId}", Update(PmFrequency.Monthly, Today().AddDays(10), active: false));

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Empty(await TasksAsync());

        (await _client.PostAsync("/api/pm/generate", null)).EnsureSuccessStatusCode();
        Assert.Empty(await TasksAsync());

        await using var db = fixture.CreateContext();
        Assert.False((await db.PmSchedules.SingleAsync(s => s.Id == _scheduleId)).IsActive);
    }

    [Fact]
    public async Task A_stopped_schedule_can_be_started_again()
    {
        await _client.PutAsJsonAsync(
            $"/api/pm/schedules/{_scheduleId}", Update(PmFrequency.Monthly, Today().AddDays(10), active: false));

        var res = await _client.PutAsJsonAsync(
            $"/api/pm/schedules/{_scheduleId}", Update(PmFrequency.Quarterly, Today().AddDays(15)));

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var tasks = await TasksAsync();
        Assert.Single(tasks);
        Assert.Equal(Today().AddDays(15), tasks[0].DueDate);
    }

    [Fact]
    public async Task A_custom_interval_is_kept_in_days_and_a_named_one_carries_none()
    {
        var custom = await _client.PutAsJsonAsync(
            $"/api/pm/schedules/{_scheduleId}", Update(PmFrequency.Custom, Today().AddDays(5), interval: 45));
        Assert.Equal(HttpStatusCode.NoContent, custom.StatusCode);

        await using (var db = fixture.CreateContext())
        {
            var s = await db.PmSchedules.SingleAsync(x => x.Id == _scheduleId);
            Assert.Equal(PmFrequency.Custom, s.Frequency);
            Assert.Equal(45, s.IntervalDays);
        }

        // Back to a named one, with a stray interval that must not be stored.
        var named = await _client.PutAsJsonAsync(
            $"/api/pm/schedules/{_scheduleId}", Update(PmFrequency.Yearly, Today().AddDays(5), interval: 45));
        Assert.Equal(HttpStatusCode.NoContent, named.StatusCode);

        await using var db2 = fixture.CreateContext();
        Assert.Equal(0, (await db2.PmSchedules.SingleAsync(x => x.Id == _scheduleId)).IntervalDays);
    }

    [Fact]
    public async Task Every_two_months_is_accepted()
    {
        var res = await _client.PutAsJsonAsync(
            $"/api/pm/schedules/{_scheduleId}", Update(PmFrequency.EveryTwoMonths, Today().AddDays(5)));

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.Equal(PmFrequency.EveryTwoMonths, (await db.PmSchedules.SingleAsync(s => s.Id == _scheduleId)).Frequency);
    }

    [Theory]
    [InlineData(99, 0, 7, "2026-12-01")]
    [InlineData(0, 0, 7, "2026-12-01")]
    [InlineData(90, 0, 7, "2026-12-01")]
    [InlineData(10, 0, -1, "2026-12-01")]
    [InlineData(10, 0, 91, "2026-12-01")]
    [InlineData(10, 0, 7, "1999-01-01")]
    public async Task A_change_that_makes_no_sense_is_refused_and_nothing_moves(
        int frequency, int interval, int grace, string next)
    {
        var before = (await TasksAsync()).Select(t => t.Id).ToList();

        var res = await _client.PutAsJsonAsync($"/api/pm/schedules/{_scheduleId}", new
        {
            frequency, intervalDays = interval, nextDueDate = next, graceDays = grace, isActive = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(before, (await TasksAsync()).Select(t => t.Id).ToList());
    }

    [Fact]
    public async Task The_list_says_where_the_schedule_falls_next_even_when_old_PMs_are_still_overdue()
    {
        // Quarterly from a hundred days ago, with an overdue PM still open from back then:
        // the oldest open one is in the past, but the next date the schedule falls on is not.
        var anchor = Today().AddDays(-100);
        await _client.PutAsJsonAsync(
            $"/api/pm/schedules/{_scheduleId}", Update(PmFrequency.Quarterly, anchor));

        var body = await _client.GetFromJsonAsync<JsonElement>($"/api/pm/schedules?equipmentId={_machineId}");
        var item = body.GetProperty("items").EnumerateArray().Single();

        var oldest = DateOnly.Parse(item.GetProperty("nextDueDate").GetString()!);
        var upcoming = DateOnly.Parse(item.GetProperty("nextUpcomingDate").GetString()!);

        Assert.True(oldest < Today(), "the oldest open PM is overdue");
        Assert.True(upcoming >= Today(), "the next date is from today on");
        Assert.True(upcoming.AddMonths(-3) < Today(), "and it is the very next one, not a later one");
        Assert.Equal(0, ((upcoming.Year * 12) + upcoming.Month - ((anchor.Year * 12) + anchor.Month)) % 3);
    }

    [Fact]
    public async Task A_schedule_that_does_not_exist_is_a_404()
    {
        var res = await _client.PutAsJsonAsync(
            "/api/pm/schedules/2000000000", Update(PmFrequency.Monthly, Today()));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
