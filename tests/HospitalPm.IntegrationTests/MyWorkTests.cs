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
/// "My work": the work orders waiting on the person signed in.
///
/// Assigning a fault to an engineer had no counterpart on their side. Nothing
/// showed who a work order was assigned to, and the only way to find their own
/// was to read down the hospital's whole queue.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class MyWorkTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "MyWork2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _alice = null!;
    private HttpClient _bob = null!;
    private int _aliceId;
    private int _bobId;
    private int _equipmentId;

    // What Alice has, by state.
    private int _assigned;
    private int _inProgress;
    private int _onHold;
    private int _resolved;
    private int _bobs;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var room = new Location { Code = $"MW-{suffix}", Name = $"Ward {suffix}", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();

            var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
            var equipment = new Domain.Assets.Equipment
            {
                AssetTag = $"MW-{suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
            };
            db.Equipment.Add(equipment);
            await db.SaveChangesAsync();
            _equipmentId = equipment.Id;
        }

        _admin = await SignedInAsync($"mw-adm-{suffix}", "Admin User", Roles.Admin);
        _alice = await SignedInAsync($"mw-ali-{suffix}", "Alice Engineer", Roles.Employee);
        _bob = await SignedInAsync($"mw-bob-{suffix}", "Bob Engineer", Roles.Employee);

        await using (var db = fixture.CreateContext())
        {
            _aliceId = await db.Users.Where(u => u.UserName == $"mw-ali-{suffix}").Select(u => u.Id).SingleAsync();
            _bobId = await db.Users.Where(u => u.UserName == $"mw-bob-{suffix}").Select(u => u.Id).SingleAsync();
        }

        _assigned = await ReportAndAssignAsync(_aliceId);

        _inProgress = await ReportAndAssignAsync(_aliceId);
        await MoveAsync(_inProgress, WorkOrderStatus.InProgress);

        _onHold = await ReportAndAssignAsync(_aliceId);
        await MoveAsync(_onHold, WorkOrderStatus.InProgress);
        await MoveAsync(_onHold, WorkOrderStatus.OnHold);

        // Alice has fixed this one; what is left is an administrator accepting it.
        _resolved = await ReportAndAssignAsync(_aliceId);
        await MoveAsync(_resolved, WorkOrderStatus.InProgress);
        var resolve = await _alice.PostAsJsonAsync(
            $"/api/work-orders/{_resolved}/resolve",
            new { resolutionNotes = "Replaced the flow sensor" });
        Assert.True(resolve.IsSuccessStatusCode, await resolve.Content.ReadAsStringAsync());

        _bobs = await ReportAndAssignAsync(_bobId);
    }

    private async Task<HttpClient> SignedInAsync(string userName, string fullName, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = userName, FullName = fullName, IsActive = true,
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

    private async Task<int> ReportAndAssignAsync(int assigneeId)
    {
        var created = await _admin.PostAsJsonAsync("/api/work-orders", new
        {
            equipmentId = _equipmentId,
            faultDescription = "Alarm sounds with no cause",
            priority = (int)WorkOrderPriority.Medium,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var assigned = await _admin.PostAsJsonAsync(
            $"/api/work-orders/{id}/assign", new { assignedToUserId = assigneeId });
        Assert.True(assigned.IsSuccessStatusCode, await assigned.Content.ReadAsStringAsync());
        return id;
    }

    private async Task MoveAsync(int id, WorkOrderStatus to)
    {
        var moved = await _admin.PostAsJsonAsync(
            $"/api/work-orders/{id}/status", new { status = (int)to, note = "test" });
        Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync());
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _admin?.Dispose();
        _alice?.Dispose();
        _bob?.Dispose();
        _factory?.Dispose();
    }

    private static async Task<JsonElement[]> ListAsync(HttpClient who, string query)
    {
        var page = await who.GetFromJsonAsync<JsonElement>($"/api/work-orders?pageSize=200&{query}");
        return page.GetProperty("items").EnumerateArray().ToArray();
    }

    private static int[] Ids(JsonElement[] items) =>
        items.Select(i => i.GetProperty("id").GetInt32()).ToArray();

    [Fact]
    public async Task Mine_lists_what_is_on_my_plate_and_nothing_else()
    {
        var mine = Ids(await ListAsync(_alice, "assignee=me"));

        // Assigned, being worked, and waiting on a part: hers to do.
        Assert.Contains(_assigned, mine);
        Assert.Contains(_inProgress, mine);
        Assert.Contains(_onHold, mine);

        // She has said this one is fixed, so it is no longer hers to do; and the
        // other engineer's work is not hers at all.
        Assert.DoesNotContain(_resolved, mine);
        Assert.DoesNotContain(_bobs, mine);
        Assert.Equal(3, mine.Length);
    }

    [Fact]
    public async Task Each_person_sees_their_own()
    {
        var bobs = Ids(await ListAsync(_bob, "assignee=me"));

        Assert.Equal([_bobs], bobs);
    }

    [Fact]
    public async Task Asking_for_a_status_shows_it_even_when_it_is_not_on_my_plate()
    {
        var resolved = Ids(await ListAsync(_alice, $"assignee=me&status={(int)WorkOrderStatus.Resolved}"));

        Assert.Equal([_resolved], resolved);
    }

    [Fact]
    public async Task Someone_with_nothing_assigned_gets_an_empty_list_not_everyone_elses()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var idle = await SignedInAsync($"mw-idle-{suffix}", "Idle Engineer", Roles.Employee);

        Assert.Empty(await ListAsync(idle, "assignee=me"));
    }

    [Fact]
    public async Task The_list_says_who_each_work_order_is_assigned_to()
    {
        var items = await ListAsync(_admin, $"equipmentId={_equipmentId}");

        var assigned = items.Single(i => i.GetProperty("id").GetInt32() == _assigned);
        var bobs = items.Single(i => i.GetProperty("id").GetInt32() == _bobs);

        Assert.Equal("Alice Engineer", assigned.GetProperty("assignedToName").GetString());
        Assert.Equal("Bob Engineer", bobs.GetProperty("assignedToName").GetString());
    }

    [Fact]
    public async Task An_unassigned_work_order_has_no_assignee_name()
    {
        var created = await _admin.PostAsJsonAsync("/api/work-orders", new
        {
            equipmentId = _equipmentId,
            faultDescription = "Nobody has picked this up",
            priority = (int)WorkOrderPriority.Low,
        });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var item = (await ListAsync(_admin, $"equipmentId={_equipmentId}"))
            .Single(i => i.GetProperty("id").GetInt32() == id);

        Assert.Equal(JsonValueKind.Null, item.GetProperty("assignedToName").ValueKind);
    }

    [Fact]
    public async Task The_dashboard_count_is_the_number_of_rows_the_tile_leads_to()
    {
        var count = (await _alice.GetFromJsonAsync<JsonElement>("/api/dashboard"))
            .GetProperty("workOrders").GetProperty("mine").GetInt32();
        var rows = await ListAsync(_alice, "assignee=me");

        Assert.Equal(3, count);
        Assert.Equal(rows.Length, count);

        var bobsCount = (await _bob.GetFromJsonAsync<JsonElement>("/api/dashboard"))
            .GetProperty("workOrders").GetProperty("mine").GetInt32();
        Assert.Equal(1, bobsCount);
    }
}
