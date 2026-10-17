using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.WorkOrders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Once an engineer has resolved a service request it is locked: nothing about it can be changed by anyone but the head of
/// the department, who can reopen it (with a reason) or close it. Closed and cancelled requests stay locked for everyone.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class ResolvedLockTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "ResolvedLock2026!";

    private ApiFactory _factory = null!;
    private HttpClient _head = null!;
    private HttpClient _engineer = null!;
    private int _headId;
    private int _engineerId;
    private string _suffix = null!;
    private int _typeId;
    private int _roomId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var type = new EquipmentType { Code = $"rlk-{_suffix}", Name = $"Lock type {_suffix}" };
            var room = new Domain.Locations.Location
            {
                Code = $"RLK-{_suffix}", Name = $"Lock room {_suffix}", Level = Domain.Locations.LocationLevel.Room,
            };
            db.EquipmentTypes.Add(type);
            db.Locations.Add(room);
            await db.SaveChangesAsync();
            _typeId = type.Id;
            _roomId = room.Id;
        }

        (_head, _headId) = await SignInAsync("rlk-head", Roles.BmeHead);
        (_engineer, _engineerId) = await SignInAsync("rlk-eng", Roles.BmeEngineer);
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

    /// <summary>A fault that has been through In progress and Resolved, as a real one has.</summary>
    private async Task<int> ResolvedAsync(string name)
    {
        await using var db = fixture.CreateContext();
        var machine = new Domain.Assets.Equipment
        {
            AssetTag = $"RLK-{name}-{_suffix}".ToUpperInvariant(), EquipmentTypeId = _typeId, LocationId = _roomId,
        };
        db.Equipment.Add(machine);
        await db.SaveChangesAsync();

        var raised = DateTime.UtcNow.AddHours(-5);
        var order = new WorkOrder
        {
            EquipmentId = machine.Id,
            FaultDescription = $"Lock test {name}",
            Priority = WorkOrderPriority.Medium,
            ReportedByUserId = _headId,
            ReportedAtUtc = raised,
            AssignedToUserId = _engineerId,
        };
        db.WorkOrders.Add(order);
        await db.SaveChangesAsync();

        order.Status = WorkOrderStatus.InProgress;
        order.StartedAtUtc = raised.AddMinutes(20);
        await db.SaveChangesAsync();

        order.Status = WorkOrderStatus.Resolved;
        order.ResolutionNotes = "Replaced the part";
        order.ResolvedByUserId = _engineerId;
        order.ResolvedAtUtc = raised.AddHours(2);
        await db.SaveChangesAsync();
        return order.Id;
    }

    private async Task<JsonElement> DetailAsync(HttpClient client, int id) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/work-orders/{id}");

    private static int[] Moves(JsonElement detail) =>
        detail.GetProperty("allowedTransitions").EnumerateArray().Select(t => t.GetInt32()).Order().ToArray();

    [Fact]
    public async Task A_resolved_request_cannot_be_changed_by_an_engineer_or_even_the_head_except_to_reopen_or_close()
    {
        var id = await ResolvedAsync("frozen");

        foreach (var client in new[] { _engineer, _head })
        {
            var note = await client.PostAsJsonAsync($"/api/work-orders/{id}/notes", new { body = "One more thing" });
            Assert.Equal(HttpStatusCode.Conflict, note.StatusCode);
            Assert.Contains("locked", await note.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            Assert.Equal(HttpStatusCode.Conflict,
                (await client.PutAsJsonAsync($"/api/work-orders/{id}/breakdown-type", new { breakdownType = 10 })).StatusCode);
        }

        // Handing it to someone else is the head's to do, and it is refused too.
        Assert.Equal(HttpStatusCode.Conflict,
            (await _head.PostAsJsonAsync($"/api/work-orders/{id}/assign", new { assignedToUserId = _engineerId })).StatusCode);

        // Nothing was written.
        var after = await DetailAsync(_head, id);
        Assert.Empty(after.GetProperty("notes").EnumerateArray().Where(n => n.GetProperty("body").GetString() == "One more thing"));
    }

    [Fact]
    public async Task Only_the_head_is_offered_reopen_and_close_and_an_engineer_is_refused_both()
    {
        var id = await ResolvedAsync("offer");

        Assert.Empty(Moves(await DetailAsync(_engineer, id)));
        var headMoves = Moves(await DetailAsync(_head, id));
        Assert.Equal(new[] { (int)WorkOrderStatus.InProgress, (int)WorkOrderStatus.Closed }, headMoves);

        var close = await _engineer.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = 60 });
        var reopen = await _engineer.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = 30, note = "Still broken" });
        Assert.Equal(HttpStatusCode.Forbidden, close.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, reopen.StatusCode);
        Assert.Contains("Only the head", await reopen.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(50, (await DetailAsync(_head, id)).GetProperty("status").GetInt32());   // still Resolved
    }

    [Fact]
    public async Task The_head_reopens_it_with_a_reason_written_on_the_request_and_it_can_then_be_worked_again()
    {
        var id = await ResolvedAsync("reopen");

        // A reopening needs a reason.
        Assert.Equal(HttpStatusCode.BadRequest, (await _head.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _head.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = 30, note = "  " })).StatusCode);

        var res = await _head.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = 30, note = "The alarm came back the same night" });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        var detail = await DetailAsync(_head, id);
        Assert.Equal(30, detail.GetProperty("status").GetInt32());
        Assert.Contains("The alarm came back", detail.GetProperty("notes").EnumerateArray().Last().GetProperty("body").GetString(), StringComparison.Ordinal);

        // Open again, so the work can be recorded.
        Assert.Equal(HttpStatusCode.NoContent,
            (await _engineer.PostAsJsonAsync($"/api/work-orders/{id}/notes", new { body = "Checked the cable again" })).StatusCode);
    }

    [Fact]
    public async Task The_head_closes_it_and_a_closed_request_is_locked_for_the_head_too()
    {
        var id = await ResolvedAsync("close");

        Assert.Equal(HttpStatusCode.NoContent, (await _head.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = 60 })).StatusCode);

        var detail = await DetailAsync(_head, id);
        Assert.Equal(60, detail.GetProperty("status").GetInt32());
        Assert.Empty(Moves(detail));

        Assert.Equal(HttpStatusCode.Conflict, (await _head.PostAsJsonAsync($"/api/work-orders/{id}/notes", new { body = "Late note" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _head.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = 30, note = "Reopen" })).StatusCode);
    }

    [Fact]
    public async Task An_open_request_is_unaffected_and_still_takes_notes()
    {
        await using var db = fixture.CreateContext();
        var machine = new Domain.Assets.Equipment
        {
            AssetTag = $"RLK-OPEN-{_suffix}".ToUpperInvariant(), EquipmentTypeId = _typeId, LocationId = _roomId,
        };
        db.Equipment.Add(machine);
        await db.SaveChangesAsync();
        var order = new WorkOrder
        {
            EquipmentId = machine.Id, FaultDescription = "Still open", Priority = WorkOrderPriority.Low,
            ReportedByUserId = _headId, ReportedAtUtc = DateTime.UtcNow.AddHours(-1), AssignedToUserId = _engineerId,
        };
        db.WorkOrders.Add(order);
        await db.SaveChangesAsync();

        Assert.Equal(HttpStatusCode.NoContent,
            (await _engineer.PostAsJsonAsync($"/api/work-orders/{order.Id}/notes", new { body = "On my way" })).StatusCode);
    }
}
