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
/// Cancelling a work order is an administrator's decision.
///
/// It throws a reported fault out of the queue and there is no way back: nothing
/// moves out of Cancelled. An Employee could cancel a Critical fault assigned to
/// someone else and the server answered 204. Found walking the PM round as an
/// Employee.
///
/// The same walk found the other half: cancelling never closed the downtime
/// window, and the dashboard counts a machine as down from that window rather
/// than from the order's status, so a cancelled machine-down fault left the
/// machine "down" for good with no order left to clear it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class WorkOrderCancelTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "CancelOrders2026!";

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
            var room = new Location { Code = $"WOC-{suffix}", Name = $"Ward {suffix}", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();

            var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
            var equipment = new Domain.Assets.Equipment
            {
                AssetTag = $"WOC-{suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
            };
            db.Equipment.Add(equipment);
            await db.SaveChangesAsync();
            _equipmentId = equipment.Id;
        }

        _admin = await SignedInAsync($"woc-adm-{suffix}", Roles.BmeHead);
        _employee = await SignedInAsync($"woc-emp-{suffix}", Roles.BmeEngineer);
    }

    private async Task<HttpClient> SignedInAsync(string userName, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = userName, FullName = userName, IsActive = true,
            };
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

    private async Task<int> ReportCriticalMachineDownAsync()
    {
        var created = await _employee.PostAsJsonAsync("/api/work-orders", new
        {
            equipmentId = _equipmentId,
            faultDescription = "Fails self test on power up",
            priority = (int)WorkOrderPriority.Critical,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static Task<HttpResponseMessage> MoveAsync(HttpClient who, int id, WorkOrderStatus to) =>
        who.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = (int)to, note = "test" });

    private static async Task<JsonElement> DetailAsync(HttpClient who, int id) =>
        await who.GetFromJsonAsync<JsonElement>($"/api/work-orders/{id}");

    private static async Task<int> MachinesDownAsync(HttpClient who) =>
        (await who.GetFromJsonAsync<JsonElement>("/api/dashboard"))
            .GetProperty("workOrders").GetProperty("machinesDown").GetInt32();

    [Fact]
    public async Task An_employee_cannot_cancel_a_work_order()
    {
        var id = await ReportCriticalMachineDownAsync();

        var refused = await MoveAsync(_employee, id, WorkOrderStatus.Cancelled);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("administrator", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        // And nothing changed: still open, still down.
        var order = await DetailAsync(_admin, id);
        Assert.Equal((int)WorkOrderStatus.Reported, order.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Null, order.GetProperty("backInServiceAtUtc").ValueKind);
    }

    [Fact]
    public async Task An_employee_can_still_do_the_rest_of_the_job()
    {
        var id = await ReportCriticalMachineDownAsync();

        // Picking up their own ticket, and the note box, must keep working:
        // the restriction is on cancelling, not on working the order.
        Assert.Equal(HttpStatusCode.NoContent, (await MoveAsync(_employee, id, WorkOrderStatus.InProgress)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await MoveAsync(_employee, id, WorkOrderStatus.OnHold)).StatusCode);
    }

    [Fact]
    public async Task Cancelling_is_not_offered_to_an_employee_but_is_to_an_administrator()
    {
        var id = await ReportCriticalMachineDownAsync();

        static int[] Offered(JsonElement order) =>
            order.GetProperty("allowedTransitions").EnumerateArray().Select(e => e.GetInt32()).ToArray();

        // The list the web page renders its buttons from, so the button is not
        // shown only to be refused.
        Assert.DoesNotContain((int)WorkOrderStatus.Cancelled, Offered(await DetailAsync(_employee, id)));
        Assert.Contains((int)WorkOrderStatus.Cancelled, Offered(await DetailAsync(_admin, id)));

        // Everything else is still on offer to the Employee.
        Assert.Contains((int)WorkOrderStatus.InProgress, Offered(await DetailAsync(_employee, id)));
    }

    [Fact]
    public async Task An_administrator_can_cancel_and_the_machine_stops_counting_as_down()
    {
        var before = await MachinesDownAsync(_admin);
        var id = await ReportCriticalMachineDownAsync();
        Assert.Equal(before + 1, await MachinesDownAsync(_admin));

        var cancelled = await MoveAsync(_admin, id, WorkOrderStatus.Cancelled);
        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);

        // The downtime window is closed by the cancellation. It used to stay open
        // for ever, because only resolving the order closed it and a cancelled
        // order can never be resolved.
        var order = await DetailAsync(_admin, id);
        Assert.Equal((int)WorkOrderStatus.Cancelled, order.GetProperty("status").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, order.GetProperty("backInServiceAtUtc").ValueKind);
        Assert.Equal(before, await MachinesDownAsync(_admin));
    }

    [Fact]
    public async Task A_cancelled_report_is_no_outage_and_shows_no_downtime()
    {
        var id = await ReportCriticalMachineDownAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await MoveAsync(_admin, id, WorkOrderStatus.Cancelled)).StatusCode);

        // Every report starts the clock, and cancelling stops it; but a report cancelled as raised in
        // error was never an outage, so it is not counted or shown as one.
        var order = await DetailAsync(_admin, id);
        Assert.NotEqual(JsonValueKind.Null, order.GetProperty("backInServiceAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, order.GetProperty("downtimeHours").ValueKind);
        Assert.False(order.GetProperty("stillDown").GetBoolean());
    }
}
