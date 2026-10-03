using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.WorkOrders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// How long a machine was down: from the moment anyone reports it is not working until it is back in
/// use, in hours, and so how long it was up.
/// </summary>
public sealed class DowntimeCalculationTests
{
    private static readonly DateTime Now = new(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_closed_window_is_the_time_between_its_ends()
    {
        var minutes = Downtime.Minutes([new(Now.AddHours(-5), Now.AddHours(-2))], Now);

        Assert.Equal(180, minutes);
        Assert.Equal(3.0, Downtime.Hours(minutes));
    }

    [Fact]
    public void A_window_still_open_counts_up_to_now()
    {
        Assert.Equal(90, Downtime.Minutes([new(Now.AddMinutes(-90), null)], Now));
    }

    [Fact]
    public void Two_reports_that_overlap_are_one_outage()
    {
        // 10:00-14:00 and 12:00-16:00 are six hours of downtime, not eight.
        var minutes = Downtime.Minutes(
        [
            new(Now.AddHours(-10), Now.AddHours(-6)),
            new(Now.AddHours(-8), Now.AddHours(-4)),
        ], Now);

        Assert.Equal(6 * 60, minutes);
    }

    [Fact]
    public void Separate_outages_add_up()
    {
        var minutes = Downtime.Minutes(
        [
            new(Now.AddHours(-10), Now.AddHours(-9)),
            new(Now.AddHours(-3), Now.AddHours(-1)),
        ], Now);

        Assert.Equal(3 * 60, minutes);
    }

    [Fact]
    public void Only_the_part_after_the_start_of_the_period_counts()
    {
        var minutes = Downtime.Minutes([new(Now.AddHours(-10), Now.AddHours(-4))], Now, sinceUtc: Now.AddHours(-6));

        Assert.Equal(2 * 60, minutes);
    }

    [Fact]
    public void Nothing_is_counted_after_now()
    {
        Assert.Equal(0, Downtime.Minutes([new(Now.AddHours(1), Now.AddHours(3))], Now));
    }

    [Fact]
    public void Hours_are_shown_to_two_places()
    {
        Assert.Equal(5.25, Downtime.Hours(315));
        Assert.Equal(0.08, Downtime.Hours(5));
    }
}

[Collection(nameof(PostgresCollection))]
public sealed class DowntimeApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "Downtime2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private int _equipmentId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var room = new Location { Code = $"DT-{suffix}", Name = $"Ward {suffix}", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();

            var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
            var equipment = new Domain.Assets.Equipment
            {
                AssetTag = $"DT-{suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
            };
            db.Equipment.Add(equipment);
            await db.SaveChangesAsync();
            _equipmentId = equipment.Id;
        }

        _admin = await SignedInAsync($"dt-adm-{suffix}", Roles.BmeHead);
        _employee = await SignedInAsync($"dt-emp-{suffix}", Roles.BmeEngineer);
    }

    private async Task<HttpClient> SignedInAsync(string userName, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser { UserName = userName, FullName = userName, IsActive = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, role);
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

    private async Task<int> ReportAsync(HttpClient who)
    {
        // Nothing says the machine is down: reporting it is saying it is not working.
        var created = await who.PostAsJsonAsync("/api/work-orders", new
        {
            equipmentId = _equipmentId,
            faultDescription = "Will not power on",
            priority = (int)WorkOrderPriority.Medium,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static Task<JsonElement> DetailAsync(HttpClient who, int id) =>
        who.GetFromJsonAsync<JsonElement>($"/api/work-orders/{id}");

    private static Task<HttpResponseMessage> MoveAsync(HttpClient who, int id, WorkOrderStatus to) =>
        who.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = (int)to, note = "test" });

    private static Task<HttpResponseMessage> ResolveAsync(HttpClient who, int id) =>
        who.PostAsJsonAsync($"/api/work-orders/{id}/resolve", new { resolutionNotes = "Replaced the power supply" });

    private async Task<JsonElement> SummaryAsync() =>
        (await _admin.GetFromJsonAsync<JsonElement>($"/api/equipment/{_equipmentId}/history")).GetProperty("summary");

    [Fact]
    public async Task Reporting_a_fault_starts_the_downtime_with_no_tick_box()
    {
        var id = await ReportAsync(_employee);

        var order = await DetailAsync(_admin, id);
        Assert.NotEqual(JsonValueKind.Null, order.GetProperty("outOfServiceAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, order.GetProperty("backInServiceAtUtc").ValueKind);
        Assert.True(order.GetProperty("stillDown").GetBoolean());
        Assert.True(order.GetProperty("downtimeHours").GetDouble() >= 0);
        Assert.True((await SummaryAsync()).GetProperty("currentlyDown").GetBoolean());
    }

    [Fact]
    public async Task Resolving_it_stops_the_downtime_and_the_machine_is_up_again()
    {
        var id = await ReportAsync(_employee);
        Assert.Equal(HttpStatusCode.NoContent, (await MoveAsync(_admin, id, WorkOrderStatus.InProgress)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ResolveAsync(_admin, id)).StatusCode);

        var order = await DetailAsync(_admin, id);
        Assert.NotEqual(JsonValueKind.Null, order.GetProperty("backInServiceAtUtc").ValueKind);
        Assert.False(order.GetProperty("stillDown").GetBoolean());
        Assert.False((await SummaryAsync()).GetProperty("currentlyDown").GetBoolean());
    }

    [Fact]
    public async Task Reopening_a_resolved_fault_puts_the_machine_back_to_down()
    {
        var id = await ReportAsync(_employee);
        await MoveAsync(_admin, id, WorkOrderStatus.InProgress);
        await ResolveAsync(_admin, id);

        Assert.Equal(HttpStatusCode.NoContent, (await MoveAsync(_admin, id, WorkOrderStatus.InProgress)).StatusCode);

        var order = await DetailAsync(_admin, id);
        Assert.Equal(JsonValueKind.Null, order.GetProperty("backInServiceAtUtc").ValueKind);
        Assert.True(order.GetProperty("stillDown").GetBoolean());
    }

    [Fact]
    public async Task The_machines_page_gives_downtime_and_uptime_in_hours()
    {
        // Two reports of one machine, overlapping: one outage. The machine has been on the register 20 days.
        await using (var db = fixture.CreateContext())
        {
            var now = DateTime.UtcNow;
            await db.Equipment.Where(e => e.Id == _equipmentId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.CreatedAtUtc, now.AddDays(-20)));
            var reporter = await db.Users.FirstAsync();
            db.WorkOrders.AddRange(
                new WorkOrder
                {
                    EquipmentId = _equipmentId, FaultDescription = "A", ReportedByUserId = reporter.Id, ReportedAtUtc = now.AddHours(-10),
                    OutOfServiceAtUtc = now.AddHours(-10), BackInServiceAtUtc = now.AddHours(-6),
                },
                new WorkOrder
                {
                    EquipmentId = _equipmentId, FaultDescription = "B", ReportedByUserId = reporter.Id, ReportedAtUtc = now.AddHours(-8),
                    OutOfServiceAtUtc = now.AddHours(-8), BackInServiceAtUtc = now.AddHours(-4),
                });
            await db.SaveChangesAsync();
        }

        var summary = await SummaryAsync();

        // 10h ago to 4h ago is six hours, not the eight the two tickets add up to.
        Assert.InRange(summary.GetProperty("totalDowntimeHours").GetDouble(), 5.99, 6.01);
        Assert.InRange(summary.GetProperty("downtimeHoursLast30Days").GetDouble(), 5.99, 6.01);
        Assert.False(summary.GetProperty("currentlyDown").GetBoolean());

        // Up for the rest of the 20 days it has been on the register: 480 hours less the six down.
        Assert.InRange(summary.GetProperty("uptimeHoursLast30Days").GetDouble(), 473.9, 474.1);
        Assert.InRange(summary.GetProperty("availabilityPercentLast30Days").GetDouble(), 98.7, 98.8);
    }

    [Fact]
    public async Task A_report_cancelled_in_error_is_not_counted_as_downtime()
    {
        var id = await ReportAsync(_employee);
        Assert.Equal(HttpStatusCode.NoContent, (await MoveAsync(_admin, id, WorkOrderStatus.Cancelled)).StatusCode);

        var summary = await SummaryAsync();
        Assert.Equal(0, summary.GetProperty("totalDowntimeHours").GetDouble());
        Assert.False(summary.GetProperty("currentlyDown").GetBoolean());
    }
}
