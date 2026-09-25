using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.Maintenance;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// PM dates that are picked one at a time instead of following a pattern.
///
/// The administrator selects a date and adds it as a PM, then selects another and adds that, and
/// so on, either while adding the machine or later on the machine's page. Each date is a PM of its
/// own. There is no frequency to work the rest out from, so the nightly job leaves these alone.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PmManualDatesTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "PmManual2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private string _suffix = null!;
    private int _typeId;
    private int _roomId;
    private int _templateId;
    private int _otherTypeTemplateId;

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var room = new Location { Code = $"MD-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();
            _roomId = room.Id;

            var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
            var other = await db.EquipmentTypes.FirstAsync(t => t.Code == "ultrasound-scanner");
            _typeId = type.Id;

            var mine = new ChecklistTemplate { EquipmentTypeId = type.Id, Code = $"md-{_suffix}", Name = "Manual PM" };
            var otherTemplate = new ChecklistTemplate { EquipmentTypeId = other.Id, Code = $"md-o-{_suffix}", Name = "Other PM" };
            db.ChecklistTemplates.AddRange(mine, otherTemplate);
            await db.SaveChangesAsync();
            _templateId = mine.Id;
            _otherTypeTemplateId = otherTemplate.Id;
        }

        _admin = await SignedInAsync($"md-adm-{_suffix}", Roles.Admin);
        _employee = await SignedInAsync($"md-emp-{_suffix}", Roles.Employee);
    }

    private async Task<HttpClient> SignedInAsync(string userName, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = userName, FullName = userName, IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _admin?.Dispose();
        _employee?.Dispose();
        _factory?.Dispose();
    }

    private string Tag(string kind) => $"MD-{kind}-{_suffix}";

    private object NewMachine(string tag, IEnumerable<DateOnly>? dates, int frequency = 95, int? template = null, int grace = 7) => new
    {
        assetTag = tag,
        equipmentTypeId = _typeId,
        locationId = _roomId,
        pm = new
        {
            checklistTemplateId = template ?? _templateId,
            frequency,
            // Ignored for hand-picked dates; a repeating schedule would start here.
            firstDueDate = Iso(Today().AddDays(400)),
            graceDays = grace,
            dates = dates?.Select(Iso).ToArray(),
        },
    };

    private async Task<int> MachineAsync(string kind, bool contract = false, EquipmentStatus status = EquipmentStatus.InService)
    {
        await using var db = fixture.CreateContext();
        var machine = new Domain.Assets.Equipment
        {
            AssetTag = Tag(kind), EquipmentTypeId = _typeId, LocationId = _roomId, Status = status,
            MaintenanceContractType = contract ? MaintenanceContractType.Amc : null,
            MaintenanceVendor = contract ? "Philips Healthcare" : null,
            MaintenanceStartDate = contract ? new DateOnly(2026, 1, 1) : null,
            MaintenanceEndDate = contract ? new DateOnly(2026, 12, 31) : null,
        };
        db.Equipment.Add(machine);
        await db.SaveChangesAsync();
        return machine.Id;
    }

    private object Dates(int machine, IEnumerable<DateOnly>? dates, int? template = null, int grace = 7, int by = 10) => new
    {
        equipmentId = machine,
        checklistTemplateId = template ?? _templateId,
        dates = dates?.Select(Iso).ToArray(),
        graceDays = grace,
        performedBy = by,
    };

    private async Task<List<PmTask>> TasksAsync(int machineId)
    {
        await using var db = fixture.CreateContext();
        return await db.PmTasks.AsNoTracking().Where(t => t.EquipmentId == machineId).OrderBy(t => t.DueDate).ToListAsync();
    }

    // ------------------------------------------------------------- while adding the machine

    [Fact]
    public async Task Each_date_picked_while_adding_a_machine_is_a_PM_of_its_own()
    {
        var picked = new[] { Today().AddDays(20), Today().AddDays(200), Today().AddDays(45) };

        var created = await _admin.PostAsJsonAsync("/api/equipment", NewMachine(Tag("A"), picked));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var tasks = await TasksAsync(id);
        Assert.Equal(picked.Order().ToList(), tasks.Select(t => t.DueDate).ToList());
        Assert.All(tasks, t => Assert.Equal(PmTaskStatus.Scheduled, t.Status));

        await using var db = fixture.CreateContext();
        var schedule = await db.PmSchedules.SingleAsync(s => s.EquipmentId == id);
        Assert.Equal(PmFrequency.Manual, schedule.Frequency);
        Assert.Equal(0, schedule.IntervalDays);
        // The earliest date, not the first-due date that was sent and is not used.
        Assert.Equal(picked.Min(), schedule.AnchorDate);
    }

    [Fact]
    public async Task A_date_that_has_passed_is_overdue_and_one_today_is_due()
    {
        var picked = new[] { Today().AddDays(-40), Today(), Today().AddDays(30) };

        var created = await _admin.PostAsJsonAsync("/api/equipment", NewMachine(Tag("S"), picked, grace: 7));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var tasks = await TasksAsync(id);
        Assert.Equal(
            new[] { PmTaskStatus.Overdue, PmTaskStatus.Due, PmTaskStatus.Scheduled },
            tasks.Select(t => t.Status).ToArray());
    }

    [Fact]
    public async Task The_same_date_picked_twice_is_one_PM()
    {
        var day = Today().AddDays(10);

        var created = await _admin.PostAsJsonAsync("/api/equipment", NewMachine(Tag("D"), [day, day]));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Single(await TasksAsync(id));
    }

    [Fact]
    public async Task Hand_picked_PMs_are_left_alone_by_the_nightly_job()
    {
        // A date long past. If the job treated this as a pattern starting there, it would fill
        // in a PM for every quarter since; it must add nothing.
        var created = await _admin.PostAsJsonAsync("/api/equipment", NewMachine(Tag("G"), [Today().AddDays(-400)]));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Single(await TasksAsync(id));

        (await _admin.PostAsync("/api/pm/generate", null)).EnsureSuccessStatusCode();

        Assert.Single(await TasksAsync(id));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("empty")]
    [InlineData("too many")]
    [InlineData("not a date")]
    public async Task Dates_that_make_no_sense_are_refused_and_no_machine_is_added(string case_)
    {
        IEnumerable<DateOnly>? dates = case_ switch
        {
            "none" => null,
            "empty" => [],
            "too many" => Enumerable.Range(1, 61).Select(n => Today().AddDays(n)),
            _ => [new DateOnly(1999, 1, 1)],
        };
        var tag = Tag($"R{case_.Length}{case_[0]}");

        var refused = await _admin.PostAsJsonAsync("/api/equipment", NewMachine(tag, dates));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.False(await db.Equipment.AnyAsync(e => e.AssetTag == tag));
    }

    // ------------------------------------------------------------- later, on a machine that exists

    [Fact]
    public async Task Dates_can_be_added_to_a_machine_that_already_exists_and_then_more_later()
    {
        var machine = await MachineAsync("E1");
        var first = new[] { Today().AddDays(10), Today().AddDays(70) };

        var one = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, first));
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        var body = await one.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, body.GetProperty("added").GetInt32());
        var scheduleId = body.GetProperty("scheduleId").GetInt32();

        // Later, two more, one of them a date already there.
        var more = new[] { first[1], Today().AddDays(130) };
        var two = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, more));
        var second = await two.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, second.GetProperty("added").GetInt32());
        Assert.Equal(1, second.GetProperty("alreadyThere").GetInt32());
        // The same schedule, not a new one.
        Assert.Equal(scheduleId, second.GetProperty("scheduleId").GetInt32());

        Assert.Equal(
            new[] { first[0], first[1], more[1] },
            (await TasksAsync(machine)).Select(t => t.DueDate).ToArray());

        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.PmSchedules.CountAsync(s => s.EquipmentId == machine));
    }

    [Fact]
    public async Task Adding_the_same_dates_again_changes_nothing()
    {
        var machine = await MachineAsync("E2");
        var dates = new[] { Today().AddDays(10) };
        await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, dates));

        var again = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, dates));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(0, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added").GetInt32());
        Assert.Single(await TasksAsync(machine));
    }

    [Fact]
    public async Task A_machine_with_a_repeating_schedule_for_the_checklist_keeps_its_pattern()
    {
        var machine = await MachineAsync("E3");
        await using (var db = fixture.CreateContext())
        {
            db.PmSchedules.Add(new PmSchedule
            {
                EquipmentId = machine, ChecklistTemplateId = _templateId, Frequency = PmFrequency.Yearly,
                AnchorDate = Today().AddYears(3), GraceDays = 7,
            });
            await db.SaveChangesAsync();
        }

        var res = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, [Today().AddDays(10)]));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Empty(await TasksAsync(machine));
    }

    [Fact]
    public async Task The_vendor_can_be_chosen_only_on_a_machine_with_a_contract_and_cannot_be_swapped_later()
    {
        var bare = await MachineAsync("V1");
        var refused = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(bare, [Today().AddDays(10)], by: 20));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var covered = await MachineAsync("V2", contract: true);
        Assert.Equal(HttpStatusCode.OK,
            (await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(covered, [Today().AddDays(10)], by: 20))).StatusCode);

        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(PmPerformedBy.Vendor, (await db.PmSchedules.SingleAsync(s => s.EquipmentId == covered)).PerformedBy);
        }

        var swap = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(covered, [Today().AddDays(50)], by: 10));
        Assert.Equal(HttpStatusCode.Conflict, swap.StatusCode);
    }

    [Fact]
    public async Task A_condemned_machine_is_never_scheduled_again()
    {
        var machine = await MachineAsync("C1", status: EquipmentStatus.Condemned);

        var res = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, [Today().AddDays(10)]));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Empty(await TasksAsync(machine));
    }

    [Fact]
    public async Task Things_that_do_not_fit_are_refused_and_nothing_is_added()
    {
        var machine = await MachineAsync("B1");
        var ok = new[] { Today().AddDays(10) };

        var noDates = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, null));
        var emptyDates = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, []));
        var unknownMachine = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(2_000_000_000, ok));
        var unknownTemplate = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, ok, template: 2_000_000_000));
        var wrongType = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, ok, template: _otherTypeTemplateId));
        var badGrace = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, ok, grace: 91));

        Assert.All(
            new[] { noDates, emptyDates, unknownMachine, unknownTemplate, wrongType, badGrace },
            r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
        Assert.Empty(await TasksAsync(machine));

        await using var db = fixture.CreateContext();
        Assert.False(await db.PmSchedules.AnyAsync(s => s.EquipmentId == machine));
    }

    [Fact]
    public async Task Only_an_administrator_can_add_dates()
    {
        var machine = await MachineAsync("F1");

        var res = await _employee.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, [Today().AddDays(10)]));

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ------------------------------------------------------------- what it does not touch

    [Fact]
    public async Task The_pattern_based_routes_refuse_a_schedule_with_no_pattern()
    {
        var machine = await MachineAsync("P1");

        var single = await _admin.PostAsJsonAsync("/api/pm/schedules", new
        {
            equipmentId = machine, checklistTemplateId = _templateId, frequency = 95, intervalDays = 0,
            anchorDate = Iso(Today()), graceDays = 7,
        });
        var bulk = await _admin.PostAsJsonAsync("/api/pm/schedules/bulk", new
        {
            checklistTemplateId = _templateId, frequency = 95, intervalDays = 0, anchorDate = Iso(Today()),
            graceDays = 7, locationId = (int?)null, includeInStore = false,
        });
        var preview = await _admin.GetAsync($"/api/pm/preview?frequency=95&anchorDate={Iso(Today())}");

        Assert.Equal(HttpStatusCode.BadRequest, single.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bulk.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
    }

    [Fact]
    public async Task A_hand_picked_schedule_has_no_pattern_to_change()
    {
        var machine = await MachineAsync("U1");
        var added = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, [Today().AddDays(10)]));
        var scheduleId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scheduleId").GetInt32();

        var res = await _admin.PutAsJsonAsync($"/api/pm/schedules/{scheduleId}", new
        {
            frequency = 20, intervalDays = 0, nextDueDate = Iso(Today().AddDays(10)), graceDays = 7, isActive = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Single(await TasksAsync(machine));
    }

    [Fact]
    public async Task The_schedule_list_shows_a_hand_picked_schedule_without_a_next_date_from_a_pattern()
    {
        var machine = await MachineAsync("L1");
        await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, [Today().AddDays(10)]));

        var list = await _admin.GetFromJsonAsync<JsonElement>($"/api/pm/schedules?equipmentId={machine}");

        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal((int)PmFrequency.Manual, item.GetProperty("frequency").GetInt32());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("nextUpcomingDate").ValueKind);
        Assert.Equal(Iso(Today().AddDays(10)), item.GetProperty("nextDueDate").GetString());
    }

    [Fact]
    public async Task Hand_picked_PMs_are_on_the_work_list_like_any_other()
    {
        var machine = await MachineAsync("W1");
        await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, [Today().AddDays(3)]));

        var list = await _employee.GetFromJsonAsync<JsonElement>($"/api/pm/tasks?equipmentId={machine}");

        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(Iso(Today().AddDays(3)), item.GetProperty("dueDate").GetString());
    }
}
