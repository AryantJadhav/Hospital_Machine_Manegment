using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.WorkOrders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>An engineer's response time (raised to In progress) and a machine's expected life: the rules, with no database.</summary>
public sealed class ResponseAndLifeRuleTests
{
    private static readonly DateTime Raised = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_fault_nobody_has_started_is_waiting_until_the_time_runs_out_and_then_overdue()
    {
        var waiting = ResponseRules.Assess(Raised, null, 60, WorkOrderStatus.Reported, Raised.AddMinutes(59));
        Assert.Equal(ResponseState.Waiting, waiting.State);
        Assert.Equal(Raised.AddMinutes(60), waiting.DueAtUtc);

        var overdue = ResponseRules.Assess(Raised, null, 60, WorkOrderStatus.Assigned, Raised.AddMinutes(95));
        Assert.Equal(ResponseState.Overdue, overdue.State);
        Assert.Equal(35, overdue.OverdueMinutes);
    }

    [Fact]
    public void Starting_exactly_on_time_is_on_time_and_one_minute_later_is_missed()
    {
        var met = ResponseRules.Assess(Raised, Raised.AddMinutes(60), 60, WorkOrderStatus.InProgress, Raised.AddHours(3));
        Assert.Equal(ResponseState.Met, met.State);
        Assert.Equal(60, met.ResponseMinutes);

        var missed = ResponseRules.Assess(Raised, Raised.AddMinutes(61), 60, WorkOrderStatus.InProgress, Raised.AddHours(3));
        Assert.Equal(ResponseState.Missed, missed.State);
        Assert.Equal(61, missed.ResponseMinutes);
    }

    [Fact]
    public void The_response_is_kept_after_the_fault_is_fixed_and_a_fault_never_started_is_not_judged()
    {
        var fixedLater = ResponseRules.Assess(Raised, Raised.AddMinutes(20), 60, WorkOrderStatus.Closed, Raised.AddDays(2));
        Assert.Equal(ResponseState.Met, fixedLater.State);

        var neverStarted = ResponseRules.Assess(Raised, null, 60, WorkOrderStatus.Resolved, Raised.AddDays(2));
        Assert.Equal(ResponseState.NotApplicable, neverStarted.State);

        var cancelled = ResponseRules.Assess(Raised, null, 60, WorkOrderStatus.Cancelled, Raised.AddDays(2));
        Assert.Equal(ResponseState.NotApplicable, cancelled.State);
    }

    [Fact]
    public void No_target_means_nothing_is_judged_but_the_time_taken_is_still_told()
    {
        var a = ResponseRules.Assess(Raised, Raised.AddMinutes(25), null, WorkOrderStatus.InProgress, Raised.AddHours(1));
        Assert.Equal(ResponseState.NotApplicable, a.State);
        Assert.Equal(25, a.ResponseMinutes);
        Assert.Null(a.DueAtUtc);
    }

    [Fact]
    public void A_clock_set_wrong_never_gives_a_response_before_the_fault_was_raised()
    {
        var a = ResponseRules.Assess(Raised, Raised.AddMinutes(-10), 60, WorkOrderStatus.InProgress, Raised.AddHours(1));
        Assert.Equal(0, a.ResponseMinutes);
        Assert.Equal(ResponseState.Met, a.State);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(43200, true)]
    [InlineData(43201, false)]
    [InlineData(-5, false)]
    public void A_response_time_is_from_one_minute_to_thirty_days(int minutes, bool allowed) =>
        Assert.Equal(allowed, ResponseRules.Problem(minutes) is null);

    [Fact]
    public void Every_priority_has_a_starting_target_and_the_more_urgent_is_the_shorter()
    {
        Assert.Equal(4, ResponseRules.Defaults.Count);
        Assert.True(ResponseRules.Defaults[WorkOrderPriority.Critical] < ResponseRules.Defaults[WorkOrderPriority.High]);
        Assert.True(ResponseRules.Defaults[WorkOrderPriority.High] < ResponseRules.Defaults[WorkOrderPriority.Medium]);
        Assert.True(ResponseRules.Defaults[WorkOrderPriority.Medium] < ResponseRules.Defaults[WorkOrderPriority.Low]);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    [InlineData(-3, false)]
    public void An_expected_life_is_blank_or_one_to_sixty_years(int? years, bool allowed) =>
        Assert.Equal(allowed, EquipmentRules.LifeProblem(years) is null);

    [Fact]
    public void Life_left_is_the_expected_life_less_the_age_and_negative_once_past_it()
    {
        Assert.Equal(24, EquipmentRules.LifeRemainingMonths(10, 96));
        Assert.Equal(0, EquipmentRules.LifeRemainingMonths(10, 120));
        Assert.Equal(-24, EquipmentRules.LifeRemainingMonths(10, 144));
        Assert.Null(EquipmentRules.LifeRemainingMonths(null, 96));
        Assert.Null(EquipmentRules.LifeRemainingMonths(10, null));
    }
}

/// <summary>Response times and expected life over HTTP, against the real database.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class ResponseAndLifeApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "RespLife2026!";

    private ApiFactory _factory = null!;
    private HttpClient _head = null!;
    private HttpClient _engineer = null!;
    private int _headId;
    private string _suffix = null!;
    private int _typeId;
    private int _roomId;

    private static readonly object[] Known =
    [
        new { priority = 40, minutes = 15 }, new { priority = 30, minutes = 60 },
        new { priority = 20, minutes = 240 }, new { priority = 10, minutes = 1440 },
    ];

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var type = new EquipmentType { Code = $"rl-{_suffix}", Name = $"Resp type {_suffix}" };
            var room = new Domain.Locations.Location
            {
                Code = $"RL-{_suffix}", Name = $"Resp room {_suffix}", Level = Domain.Locations.LocationLevel.Room,
            };
            db.EquipmentTypes.Add(type);
            db.Locations.Add(room);
            await db.SaveChangesAsync();
            _typeId = type.Id;
            _roomId = room.Id;
        }

        (_head, _headId) = await SignInAsync("rl-head", Roles.BmeHead);
        (_engineer, _) = await SignInAsync("rl-eng", Roles.BmeEngineer);

        // Known targets for every test here, whatever an earlier test left.
        var set = await _head.PutAsJsonAsync("/api/work-orders/response-targets", new { targets = Known });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
    }

    private async Task<(HttpClient Client, int UserId)> SignInAsync(string prefix, string role)
    {
        int id;
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = $"{prefix}-{_suffix}", FullName = $"{prefix} person", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, role);
            id = user.Id;
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = $"{prefix}-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (client, id);
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _head?.Dispose();
        _engineer?.Dispose();
        _factory?.Dispose();
    }

    // ------------------------------------------------------------------------------------------------ response targets

    [Fact]
    public async Task Everyone_reads_the_targets_and_they_start_at_the_defaults()
    {
        var targets = await _engineer.GetFromJsonAsync<JsonElement>("/api/work-orders/response-targets");
        var byPriority = targets.GetProperty("targets").EnumerateArray()
            .ToDictionary(t => t.GetProperty("priority").GetInt32(), t => t.GetProperty("minutes").GetInt32());

        Assert.Equal(4, byPriority.Count);
        Assert.Equal(15, byPriority[40]);
        Assert.Equal(60, byPriority[30]);
        Assert.Equal(240, byPriority[20]);
        Assert.Equal(1440, byPriority[10]);
    }

    [Fact]
    public async Task The_head_changes_a_target_and_an_engineer_may_not()
    {
        try
        {
            var res = await _head.PutAsJsonAsync("/api/work-orders/response-targets", new { targets = new[] { new { priority = 40, minutes = 10 } } });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);

            var after = await _engineer.GetFromJsonAsync<JsonElement>("/api/work-orders/response-targets");
            Assert.Equal(10, after.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("priority").GetInt32() == 40).GetProperty("minutes").GetInt32());
            // The others are untouched.
            Assert.Equal(60, after.GetProperty("targets").EnumerateArray().Single(t => t.GetProperty("priority").GetInt32() == 30).GetProperty("minutes").GetInt32());

            var refused = await _engineer.PutAsJsonAsync("/api/work-orders/response-targets", new { targets = new[] { new { priority = 40, minutes = 5 } } });
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        }
        finally
        {
            await _head.PutAsJsonAsync("/api/work-orders/response-targets", new { targets = Known });
        }
    }

    [Theory]
    [InlineData(40, 0)]
    [InlineData(40, -1)]
    [InlineData(40, 43201)]
    [InlineData(99, 30)]
    public async Task A_target_that_cannot_be_right_is_refused(int priority, int minutes)
    {
        var res = await _head.PutAsJsonAsync("/api/work-orders/response-targets", new { targets = new[] { new { priority, minutes } } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Nothing_sent_or_one_priority_twice_is_refused_and_nobody_signed_out_may_read_them()
    {
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _head.PutAsJsonAsync("/api/work-orders/response-targets", new { targets = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _head.PutAsJsonAsync("/api/work-orders/response-targets", new { targets = new[] { new { priority = 40, minutes = 5 }, new { priority = 40, minutes = 6 } } })).StatusCode);

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/work-orders/response-targets")).StatusCode);
    }

    // ----------------------------------------------------------------------------------------- faults judged against them

    private async Task<(int Id, string Tag)> FaultAsync(
        string name, WorkOrderPriority priority, int raisedMinutesAgo, int? startedAfterMinutes, WorkOrderStatus final)
    {
        await using var db = fixture.CreateContext();
        var machine = new Domain.Assets.Equipment
        {
            AssetTag = $"RL-{name}-{_suffix}".ToUpperInvariant(), EquipmentTypeId = _typeId, LocationId = _roomId,
        };
        db.Equipment.Add(machine);
        await db.SaveChangesAsync();

        var raised = DateTime.UtcNow.AddMinutes(-raisedMinutesAgo);
        var order = new WorkOrder
        {
            EquipmentId = machine.Id,
            FaultDescription = $"Response test {name}",
            Priority = priority,
            ReportedByUserId = _headId,
            ReportedAtUtc = raised,
        };
        db.WorkOrders.Add(order);
        await db.SaveChangesAsync();

        if (startedAfterMinutes is { } after)
        {
            order.Status = WorkOrderStatus.InProgress;
            order.StartedAtUtc = raised.AddMinutes(after);
            await db.SaveChangesAsync();
        }

        if (final == WorkOrderStatus.Cancelled)
        {
            order.Status = WorkOrderStatus.Cancelled;
            await db.SaveChangesAsync();
        }

        return (order.Id, machine.AssetTag);
    }

    private async Task<JsonElement> ResponseOfAsync(int id)
    {
        var detail = await _engineer.GetFromJsonAsync<JsonElement>($"/api/work-orders/{id}");
        return detail.GetProperty("response");
    }

    [Fact]
    public async Task A_critical_fault_nobody_started_for_half_an_hour_is_overdue_by_the_difference()
    {
        var (id, _) = await FaultAsync("over", WorkOrderPriority.Critical, 30, null, WorkOrderStatus.Reported);

        var r = await ResponseOfAsync(id);
        Assert.Equal("Overdue", r.GetProperty("state").GetString());
        Assert.Equal(15, r.GetProperty("targetMinutes").GetInt32());
        Assert.InRange(r.GetProperty("overdueMinutes").GetInt32(), 14, 16);
    }

    [Fact]
    public async Task A_fresh_high_priority_fault_is_still_within_its_time()
    {
        var (id, _) = await FaultAsync("wait", WorkOrderPriority.High, 10, null, WorkOrderStatus.Reported);

        var r = await ResponseOfAsync(id);
        Assert.Equal("Waiting", r.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("overdueMinutes").ValueKind);
    }

    [Fact]
    public async Task A_fault_started_in_time_is_met_and_one_started_late_is_missed_both_with_the_minutes()
    {
        var (met, _) = await FaultAsync("met", WorkOrderPriority.Medium, 300, 30, WorkOrderStatus.InProgress);
        var (late, _) = await FaultAsync("late", WorkOrderPriority.High, 300, 90, WorkOrderStatus.InProgress);

        var onTime = await ResponseOfAsync(met);
        Assert.Equal("Met", onTime.GetProperty("state").GetString());
        Assert.Equal(30, onTime.GetProperty("responseMinutes").GetInt32());

        var missed = await ResponseOfAsync(late);
        Assert.Equal("Missed", missed.GetProperty("state").GetString());
        Assert.Equal(90, missed.GetProperty("responseMinutes").GetInt32());
        Assert.Equal(60, missed.GetProperty("targetMinutes").GetInt32());
    }

    [Fact]
    public async Task A_cancelled_fault_is_not_judged_and_the_list_carries_the_response_too()
    {
        var (id, tag) = await FaultAsync("cancel", WorkOrderPriority.Critical, 500, null, WorkOrderStatus.Cancelled);
        Assert.Equal("NotApplicable", (await ResponseOfAsync(id)).GetProperty("state").GetString());

        var (overId, overTag) = await FaultAsync("list", WorkOrderPriority.Critical, 45, null, WorkOrderStatus.Reported);
        var list = await _engineer.GetFromJsonAsync<JsonElement>("/api/work-orders?pageSize=200&status=10");
        var row = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == overId);
        Assert.Equal("Overdue", row.GetProperty("response").GetProperty("state").GetString());
        Assert.Equal(overTag, row.GetProperty("assetTag").GetString());
        _ = tag;
    }

    // ------------------------------------------------------------------------------------------------- expected life

    private object Machine(string name, int? installedMonthsAgo, int? expectedLifeYears, int? purchasedMonthsAgo = null)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(330));
        return new
        {
            assetTag = $"RLM-{name}-{_suffix}".ToUpperInvariant(),
            equipmentTypeId = _typeId,
            locationId = _roomId,
            purchaseDate = purchasedMonthsAgo is { } p ? today.AddMonths(-p) : (DateOnly?)null,
            installationDate = installedMonthsAgo is { } m ? today.AddMonths(-m) : (DateOnly?)null,
            expectedLifeYears,
        };
    }

    private async Task<int> CreateMachineAsync(object body)
    {
        var res = await _head.PostAsJsonAsync("/api/equipment", body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task A_machine_with_an_expected_life_says_how_much_is_left_and_how_far_past_it_is()
    {
        var young = await CreateMachineAsync(Machine("young", 96, 10));
        var old = await CreateMachineAsync(Machine("old", 144, 10));

        var a = await _engineer.GetFromJsonAsync<JsonElement>($"/api/equipment/{young}");
        Assert.Equal(10, a.GetProperty("expectedLifeYears").GetInt32());
        Assert.Equal(96, a.GetProperty("ageMonths").GetInt32());
        Assert.Equal(24, a.GetProperty("lifeRemainingMonths").GetInt32());

        var b = await _engineer.GetFromJsonAsync<JsonElement>($"/api/equipment/{old}");
        Assert.Equal(-24, b.GetProperty("lifeRemainingMonths").GetInt32());
    }

    [Fact]
    public async Task The_machines_own_page_is_told_the_age_and_the_life_left_too()
    {
        // The page reads /history, not /api/equipment/{id}: both must say it.
        var old = await CreateMachineAsync(Machine("histold", 144, 10));
        var noDate = await CreateMachineAsync(Machine("histnodate", null, 10));

        var a = await _engineer.GetFromJsonAsync<JsonElement>($"/api/equipment/{old}/history");
        Assert.Equal(10, a.GetProperty("equipment").GetProperty("expectedLifeYears").GetInt32());
        Assert.Equal(144, a.GetProperty("age").GetProperty("months").GetInt32());
        Assert.Equal("installation", a.GetProperty("age").GetProperty("from").GetString());
        Assert.Equal(-24, a.GetProperty("age").GetProperty("lifeRemainingMonths").GetInt32());

        var b = await _engineer.GetFromJsonAsync<JsonElement>($"/api/equipment/{noDate}/history");
        Assert.Equal(10, b.GetProperty("equipment").GetProperty("expectedLifeYears").GetInt32());
        Assert.Equal(JsonValueKind.Null, b.GetProperty("age").ValueKind);
    }

    [Fact]
    public async Task Without_an_expected_life_or_without_an_age_there_is_no_life_left_to_say()
    {
        var noLife = await CreateMachineAsync(Machine("nolife", 24, null));
        var noAge = await CreateMachineAsync(Machine("noage", null, 10));

        var a = await _engineer.GetFromJsonAsync<JsonElement>($"/api/equipment/{noLife}");
        Assert.Equal(JsonValueKind.Null, a.GetProperty("expectedLifeYears").ValueKind);
        Assert.Equal(JsonValueKind.Null, a.GetProperty("lifeRemainingMonths").ValueKind);

        var b = await _engineer.GetFromJsonAsync<JsonElement>($"/api/equipment/{noAge}");
        Assert.Equal(10, b.GetProperty("expectedLifeYears").GetInt32());
        Assert.Equal(JsonValueKind.Null, b.GetProperty("lifeRemainingMonths").ValueKind);
    }

    [Fact]
    public async Task An_edit_keeps_the_expected_life_when_it_is_left_out_changes_it_and_zero_removes_it()
    {
        var id = await CreateMachineAsync(Machine("edit", 12, 8));

        async Task<JsonElement> PutAsync(object body)
        {
            var res = await _head.PutAsJsonAsync($"/api/equipment/{id}", body);
            Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
            return await _engineer.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}");
        }

        // Left out entirely: kept.
        var kept = await PutAsync(new
        {
            assetTag = $"RLM-EDIT-{_suffix}".ToUpperInvariant(), equipmentTypeId = _typeId, locationId = _roomId,
            installationDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(330)).AddMonths(-12),
        });
        Assert.Equal(8, kept.GetProperty("expectedLifeYears").GetInt32());

        var changed = await PutAsync(new
        {
            assetTag = $"RLM-EDIT-{_suffix}".ToUpperInvariant(), equipmentTypeId = _typeId, locationId = _roomId,
            installationDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(330)).AddMonths(-12), expectedLifeYears = 15,
        });
        Assert.Equal(15, changed.GetProperty("expectedLifeYears").GetInt32());

        var removed = await PutAsync(new
        {
            assetTag = $"RLM-EDIT-{_suffix}".ToUpperInvariant(), equipmentTypeId = _typeId, locationId = _roomId,
            installationDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(330)).AddMonths(-12), expectedLifeYears = 0,
        });
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("expectedLifeYears").ValueKind);
    }

    [Theory]
    [InlineData(61)]
    [InlineData(-3)]
    public async Task An_expected_life_outside_one_to_sixty_years_is_refused_on_create(int years)
    {
        var res = await _head.PostAsJsonAsync("/api/equipment", Machine($"bad{years}".Replace("-", "m"), 12, years));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task The_database_itself_refuses_an_expected_life_of_ninety_nine_years()
    {
        await using var db = fixture.CreateContext();
        db.Equipment.Add(new Domain.Assets.Equipment
        {
            AssetTag = $"RLM-DB-{_suffix}".ToUpperInvariant(), EquipmentTypeId = _typeId, LocationId = _roomId, ExpectedLifeYears = 99,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
