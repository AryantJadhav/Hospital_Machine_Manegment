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
/// After an engineer resolves a fault it goes back to the department that asked, to say whether it is really fixed. Anyone in
/// that department can accept it (it is closed) or say it is not fixed (it goes back to the engineer, with a reason).
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class AcknowledgementTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "Acknowledge2026!";

    private ApiFactory _factory = null!;
    private HttpClient _head = null!;
    private HttpClient _engineer = null!;
    private HttpClient _ward = null!;
    private HttpClient _otherWard = null!;
    private int _headId;
    private int _engineerId;
    private string _suffix = null!;
    private int _typeId;
    private int _roomId;
    private int _otherRoomId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var type = new EquipmentType { Code = $"ack-{_suffix}", Name = $"Ack type {_suffix}" };
            var room = new Domain.Locations.Location
            {
                Code = $"ACK-{_suffix}", Name = $"Ack ward {_suffix}", Level = Domain.Locations.LocationLevel.Room,
            };
            var other = new Domain.Locations.Location
            {
                Code = $"ACK2-{_suffix}", Name = $"Other ward {_suffix}", Level = Domain.Locations.LocationLevel.Room,
            };
            db.EquipmentTypes.Add(type);
            db.Locations.AddRange(room, other);
            await db.SaveChangesAsync();
            _typeId = type.Id;
            _roomId = room.Id;
            _otherRoomId = other.Id;
        }

        (_head, _headId) = await SignInAsync("ack-head", Roles.BmeHead);
        (_engineer, _engineerId) = await SignInAsync("ack-eng", Roles.BmeEngineer);
        var (ward, wardId) = await SignInAsync("ack-ward", Roles.DepartmentUser);
        var (otherWard, otherWardId) = await SignInAsync("ack-ward2", Roles.DepartmentUser);
        _ward = ward;
        _otherWard = otherWard;

        // The first ward belongs to the department the machine is in; the second to a different one.
        Assert.Equal(HttpStatusCode.NoContent,
            (await _head.PutAsJsonAsync($"/api/users/{wardId}/departments", new { locationIds = new[] { _roomId } })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _head.PutAsJsonAsync($"/api/users/{otherWardId}/departments", new { locationIds = new[] { _otherRoomId } })).StatusCode);
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
        _ward?.Dispose();
        _otherWard?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>A fault in the first ward's department, taken as far as the engineer's Resolved.</summary>
    private async Task<int> ResolvedAsync(string name, bool resolve = true)
    {
        await using var db = fixture.CreateContext();
        var machine = new Domain.Assets.Equipment
        {
            AssetTag = $"ACK-{name}-{_suffix}".ToUpperInvariant(), EquipmentTypeId = _typeId, LocationId = _roomId,
        };
        db.Equipment.Add(machine);
        await db.SaveChangesAsync();

        var raised = DateTime.UtcNow.AddHours(-5);
        var order = new WorkOrder
        {
            EquipmentId = machine.Id,
            FaultDescription = $"Acknowledge test {name}",
            Priority = WorkOrderPriority.Medium,
            ReportedByUserId = _headId,
            ReportedAtUtc = raised,
            AssignedToUserId = _engineerId,
            OutOfServiceAtUtc = raised,
        };
        db.WorkOrders.Add(order);
        await db.SaveChangesAsync();

        order.Status = WorkOrderStatus.InProgress;
        order.StartedAtUtc = raised.AddMinutes(20);
        await db.SaveChangesAsync();

        if (resolve)
        {
            order.Status = WorkOrderStatus.Resolved;
            order.ResolutionNotes = "Replaced the part";
            order.ResolvedByUserId = _engineerId;
            order.ResolvedAtUtc = raised.AddHours(2);
            order.BackInServiceAtUtc = raised.AddHours(2);
            await db.SaveChangesAsync();
        }

        return order.Id;
    }

    private static async Task<JsonElement> DetailAsync(HttpClient client, int id) =>
        await client.GetFromJsonAsync<JsonElement>($"/api/work-orders/{id}");

    [Fact]
    public async Task Anyone_in_the_department_can_accept_a_resolved_fault_and_it_is_closed_and_locked()
    {
        var id = await ResolvedAsync("accept");

        var res = await _ward.PostAsJsonAsync($"/api/work-orders/{id}/acknowledge", new { accepted = true, note = "Working well now" });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        var detail = await DetailAsync(_head, id);
        Assert.Equal(60, detail.GetProperty("status").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("closedAtUtc").ValueKind);
        Assert.False(detail.GetProperty("canAcknowledge").GetBoolean());

        var note = detail.GetProperty("notes").EnumerateArray().Last();
        Assert.Contains("Accepted by the department", note.GetProperty("body").GetString(), StringComparison.Ordinal);
        Assert.Contains("Working well now", note.GetProperty("body").GetString(), StringComparison.Ordinal);

        // Closed is locked, for the department as well.
        Assert.Equal(HttpStatusCode.Conflict, (await _ward.PostAsJsonAsync($"/api/work-orders/{id}/acknowledge", new { accepted = true })).StatusCode);
    }

    [Fact]
    public async Task Saying_it_is_not_fixed_sends_it_back_to_the_engineer_with_the_reason_and_the_machine_is_down_again()
    {
        var id = await ResolvedAsync("reject");

        // The reason is needed.
        Assert.Equal(HttpStatusCode.BadRequest, (await _ward.PostAsJsonAsync($"/api/work-orders/{id}/acknowledge", new { accepted = false })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _ward.PostAsJsonAsync($"/api/work-orders/{id}/acknowledge", new { accepted = false, note = " " })).StatusCode);
        Assert.Equal(50, (await DetailAsync(_head, id)).GetProperty("status").GetInt32());

        var res = await _ward.PostAsJsonAsync($"/api/work-orders/{id}/acknowledge", new { accepted = false, note = "The alarm still sounds at night" });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        var detail = await DetailAsync(_engineer, id);
        Assert.Equal(30, detail.GetProperty("status").GetInt32());                       // back In progress
        Assert.True(detail.GetProperty("stillDown").GetBoolean());                        // down again, counted from the report
        Assert.Equal(_engineerId, detail.GetProperty("assignedToUserId").GetInt32());     // with the same engineer
        Assert.Contains("The alarm still sounds at night", detail.GetProperty("notes").EnumerateArray().Last().GetProperty("body").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_fault_s_own_department_and_never_the_engineer_who_did_the_work_may_answer()
    {
        var id = await ResolvedAsync("who");
        var body = new { accepted = true };

        // A ward in another department cannot even see it.
        Assert.Equal(HttpStatusCode.NotFound, (await _otherWard.PostAsJsonAsync($"/api/work-orders/{id}/acknowledge", body)).StatusCode);
        // The engineer does not mark their own work as accepted.
        Assert.Equal(HttpStatusCode.Forbidden, (await _engineer.PostAsJsonAsync($"/api/work-orders/{id}/acknowledge", body)).StatusCode);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/work-orders/{id}/acknowledge", body)).StatusCode);

        Assert.Equal(50, (await DetailAsync(_head, id)).GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task A_fault_that_is_not_resolved_cannot_be_answered()
    {
        var open = await ResolvedAsync("open", resolve: false);
        Assert.Equal(HttpStatusCode.Conflict, (await _ward.PostAsJsonAsync($"/api/work-orders/{open}/acknowledge", new { accepted = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _ward.PostAsJsonAsync("/api/work-orders/2000000000/acknowledge", new { accepted = true })).StatusCode);
    }

    [Fact]
    public async Task The_page_is_told_who_may_answer_and_the_ward_still_cannot_edit_a_resolved_fault()
    {
        var id = await ResolvedAsync("told");

        Assert.True((await DetailAsync(_ward, id)).GetProperty("canAcknowledge").GetBoolean());
        Assert.True((await DetailAsync(_head, id)).GetProperty("canAcknowledge").GetBoolean());
        Assert.False((await DetailAsync(_engineer, id)).GetProperty("canAcknowledge").GetBoolean());

        // Answering is all they can do to it: it is locked.
        Assert.Equal(HttpStatusCode.Conflict, (await _ward.PostAsJsonAsync($"/api/work-orders/{id}/notes", new { body = "One more thing" })).StatusCode);

        // The summary, scoped to each person, counts what is waiting.
        var summary = await _ward.GetFromJsonAsync<JsonElement>("/api/work-orders/summary");
        Assert.True(summary.GetProperty("awaitingClosure").GetInt32() >= 1);
        var other = await _otherWard.GetFromJsonAsync<JsonElement>("/api/work-orders/summary");
        Assert.Equal(0, other.GetProperty("awaitingClosure").GetInt32());
    }

    [Fact]
    public async Task The_head_can_still_close_a_resolved_fault_the_department_has_not_answered()
    {
        var id = await ResolvedAsync("headcloses");
        Assert.Equal(HttpStatusCode.NoContent, (await _head.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = 60 })).StatusCode);
        Assert.Equal(60, (await DetailAsync(_head, id)).GetProperty("status").GetInt32());
    }
}
