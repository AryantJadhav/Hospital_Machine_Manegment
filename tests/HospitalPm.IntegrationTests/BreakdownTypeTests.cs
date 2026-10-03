using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// What kind of breakdown a service request was: hardware, software, both, an accessory or consumable, or
/// improper usage. Said by whoever reports it when they know, and by the engineer once they have looked.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class BreakdownTypeTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "Breakdown2026!";

    private ApiFactory _factory = null!;
    private string _suffix = null!;
    private int _departmentId;
    private int _machineId;
    private int _otherMachineId;

    private HttpClient _head = null!;
    private HttpClient _engineer = null!;
    private HttpClient _ward = null!;
    private int _engineerId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var building = new Location { Code = $"BDB-{_suffix}", Name = $"Block {_suffix}", Level = LocationLevel.Building };
            db.Locations.Add(building);
            await db.SaveChangesAsync();

            var department = new Location { Code = $"BDD-{_suffix}", Name = $"Dept {_suffix}", Level = LocationLevel.Department, ParentId = building.Id };
            var other = new Location { Code = $"BDO-{_suffix}", Name = $"Other {_suffix}", Level = LocationLevel.Department, ParentId = building.Id };
            db.Locations.AddRange(department, other);
            await db.SaveChangesAsync();
            _departmentId = department.Id;

            var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
            var machine = new Domain.Assets.Equipment { AssetTag = $"BDM-{_suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = department.Id };
            var otherMachine = new Domain.Assets.Equipment { AssetTag = $"BDN-{_suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = other.Id };
            db.Equipment.AddRange(machine, otherMachine);
            await db.SaveChangesAsync();
            _machineId = machine.Id;
            _otherMachineId = otherMachine.Id;
        }

        (_head, _) = await SignInAsync("bd-head", Roles.BmeHead);
        (_engineer, _engineerId) = await SignInAsync("bd-eng", Roles.BmeEngineer);
        var (ward, wardId) = await SignInAsync("bd-ward", Roles.DepartmentUser);
        _ward = ward;

        Assert.Equal(HttpStatusCode.NoContent,
            (await _head.PutAsJsonAsync($"/api/users/{wardId}/departments", new { locationIds = new[] { _departmentId } })).StatusCode);
    }

    private async Task<(HttpClient Client, int UserId)> SignInAsync(string prefix, string role)
    {
        int userId;
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
            userId = user.Id;
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = $"{prefix}-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (client, userId);
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
        _factory?.Dispose();
    }

    // 10 hardware, 20 software, 30 both, 40 accessory or consumable, 50 improper usage.
    private async Task<int> RaiseAsync(int? breakdownType, HttpClient? by = null, int? machineId = null)
    {
        var res = await (by ?? _engineer).PostAsJsonAsync("/api/work-orders",
            new { equipmentId = machineId ?? _machineId, faultDescription = $"Fault {Guid.NewGuid():N} {_suffix}", priority = 20, breakdownType });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> GetAsync(int id)
    {
        var res = await _engineer.GetAsync($"/api/work-orders/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> SetAsync(int id, int? breakdownType, HttpClient? by = null) =>
        (by ?? _engineer).PutAsJsonAsync($"/api/work-orders/{id}/breakdown-type", new { breakdownType });

    private static JsonElement[] Notes(JsonElement order) => order.GetProperty("notes").EnumerateArray().ToArray();

    // ---------------------------------------------------------------- saying it when it is reported

    [Theory]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(30)]
    [InlineData(40)]
    [InlineData(50)]
    public async Task Each_of_the_five_kinds_can_be_said_when_a_fault_is_reported(int kind)
    {
        var id = await RaiseAsync(kind);

        Assert.Equal(kind, (await GetAsync(id)).GetProperty("breakdownType").GetInt32());
    }

    [Fact]
    public async Task It_can_be_left_blank_until_someone_knows()
    {
        var id = await RaiseAsync(null);

        Assert.Equal(JsonValueKind.Null, (await GetAsync(id)).GetProperty("breakdownType").ValueKind);
    }

    [Fact]
    public async Task A_kind_that_is_not_one_of_the_five_is_refused()
    {
        foreach (var kind in new[] { 0, 7, 60, -1 })
        {
            var res = await _engineer.PostAsJsonAsync("/api/work-orders",
                new { equipmentId = _machineId, faultDescription = "Will not start", priority = 20, breakdownType = kind });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task A_department_can_say_what_it_thinks_when_it_reports()
    {
        var id = await RaiseAsync(50, _ward);

        Assert.Equal(50, (await GetAsync(id)).GetProperty("breakdownType").GetInt32());
        // And what it sees is the same.
        var seen = await (await _ward.GetAsync($"/api/work-orders/{id}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(50, seen.GetProperty("breakdownType").GetInt32());
    }

    // ---------------------------------------------------------------- the engineer's say

    [Fact]
    public async Task The_engineer_says_what_kind_it_was_and_it_goes_on_the_timeline_with_who_said_it()
    {
        var id = await RaiseAsync(null, _ward);

        Assert.Equal(HttpStatusCode.NoContent, (await SetAsync(id, 10)).StatusCode);

        var order = await GetAsync(id);
        Assert.Equal(10, order.GetProperty("breakdownType").GetInt32());
        var note = Notes(order).Single();
        Assert.Equal("Breakdown type: Hardware.", note.GetProperty("body").GetString());
        Assert.Equal(_engineerId, note.GetProperty("authorUserId").GetInt32());
    }

    [Fact]
    public async Task It_can_be_changed_and_taken_off_and_each_change_is_on_the_timeline()
    {
        var id = await RaiseAsync(10);

        Assert.Equal(HttpStatusCode.NoContent, (await SetAsync(id, 30)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SetAsync(id, null)).StatusCode);

        var order = await GetAsync(id);
        Assert.Equal(JsonValueKind.Null, order.GetProperty("breakdownType").ValueKind);
        Assert.Equal(
            ["Breakdown type: Both (Hardware & Software).", "Breakdown type taken off."],
            Notes(order).Select(n => n.GetProperty("body").GetString()).ToArray());
    }

    [Fact]
    public async Task Saying_the_same_thing_again_changes_nothing_and_adds_nothing()
    {
        var id = await RaiseAsync(20);

        Assert.Equal(HttpStatusCode.NoContent, (await SetAsync(id, 20)).StatusCode);

        Assert.Empty(Notes(await GetAsync(id)));
    }

    [Fact]
    public async Task Only_those_who_work_a_request_can_change_it()
    {
        var id = await RaiseAsync(null, _ward);

        Assert.Equal(HttpStatusCode.Forbidden, (await SetAsync(id, 10, _ward)).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await GetAsync(id)).GetProperty("breakdownType").ValueKind);

        // The head can, as can anyone who works the floor.
        Assert.Equal(HttpStatusCode.NoContent, (await SetAsync(id, 40, _head)).StatusCode);
        Assert.Equal(40, (await GetAsync(id)).GetProperty("breakdownType").GetInt32());
    }

    [Fact]
    public async Task A_kind_that_is_not_one_of_the_five_cannot_be_set_and_an_unknown_request_is_not_found()
    {
        var id = await RaiseAsync(10);

        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(id, 7)).StatusCode);
        Assert.Equal(10, (await GetAsync(id)).GetProperty("breakdownType").GetInt32());

        Assert.Equal(HttpStatusCode.NotFound, (await SetAsync(2_000_000_000, 10)).StatusCode);
    }

    [Fact]
    public async Task A_request_that_is_cancelled_can_no_longer_be_changed()
    {
        var id = await RaiseAsync(10);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _head.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = 70, note = "Raised in error" })).StatusCode);

        var res = await SetAsync(id, 20);

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Equal(10, (await GetAsync(id)).GetProperty("breakdownType").GetInt32());
    }

    [Fact]
    public async Task The_database_will_not_hold_a_kind_that_is_not_one_of_the_five()
    {
        var id = await RaiseAsync(null);

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE work_order SET breakdown_type = 99 WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState);
    }

    // ---------------------------------------------------------------- finding them

    [Fact]
    public async Task The_list_can_be_narrowed_to_one_kind()
    {
        var hardware = await RaiseAsync(10);
        var software = await RaiseAsync(20);
        var blank = await RaiseAsync(null);

        async Task<List<int>> Of(int kind) =>
            (await (await _engineer.GetAsync($"/api/work-orders?breakdownType={kind}&q={_suffix}&pageSize=200")).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();

        Assert.Equal([hardware], await Of(10));
        Assert.Equal([software], await Of(20));
        Assert.Empty(await Of(50));

        var all = (await (await _engineer.GetAsync($"/api/work-orders?q={_suffix}&pageSize=200")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, all.Count);
        Assert.Equal(JsonValueKind.Null, all.Single(i => i.GetProperty("id").GetInt32() == blank).GetProperty("breakdownType").ValueKind);
        Assert.Equal(10, all.Single(i => i.GetProperty("id").GetInt32() == hardware).GetProperty("breakdownType").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await _engineer.GetAsync("/api/work-orders?breakdownType=7")).StatusCode);
    }

    [Fact]
    public async Task A_department_sees_the_kind_on_its_own_requests_only_as_the_list_scope_allows()
    {
        var mine = await RaiseAsync(30, _engineer, _machineId);
        var theirs = await RaiseAsync(30, _engineer, _otherMachineId);

        var seen = (await (await _ward.GetAsync($"/api/work-orders?breakdownType=30&q={_suffix}&pageSize=200")).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();

        Assert.Contains(mine, seen);
        Assert.DoesNotContain(theirs, seen);
    }

    // ---------------------------------------------------------------- the paper and the history

    [Fact]
    public async Task The_printed_service_report_still_prints_with_and_without_a_kind()
    {
        var with = await RaiseAsync(40);
        var without = await RaiseAsync(null);

        foreach (var id in new[] { with, without })
        {
            var res = await _engineer.GetAsync($"/api/reports/work-orders/{id}/report.pdf");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(await res.Content.ReadAsByteArrayAsync(), 0, 4));
        }
    }

    [Fact]
    public async Task The_service_history_says_what_kind_each_repair_was_on_screen_and_in_the_file()
    {
        var id = await RaiseAsync(50, _ward);
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = 30 })).StatusCode);
        Assert.True((await _engineer.PostAsJsonAsync($"/api/work-orders/{id}/resolve", new { resolutionNotes = "Shown how to use it" })).IsSuccessStatusCode);

        var history = await (await _ward.GetAsync("/api/work-orders/history?pageSize=200")).Content.ReadFromJsonAsync<JsonElement>();
        var row = history.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == id);
        Assert.Equal(50, row.GetProperty("breakdownType").GetInt32());
        Assert.Equal("Improper usage", row.GetProperty("breakdownTypeLabel").GetString());

        var csv = await (await _ward.GetAsync("/api/work-orders/history/report.csv")).Content.ReadAsStringAsync();
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains("Breakdown type", lines[0]);
        Assert.Contains(lines, l => l.Contains("Improper usage", StringComparison.Ordinal) && l.Contains("Shown how to use it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_audit_trail_says_who_changed_the_kind()
    {
        var id = await RaiseAsync(10);
        await SetAsync(id, 20);

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT changed_by FROM audit_log WHERE table_name = 'work_order' AND record_pk = @id AND operation = 'UPDATE' " +
            "AND (new_data ->> 'breakdown_type') = '20' ORDER BY id DESC LIMIT 1", conn);
        cmd.Parameters.AddWithValue("id", id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(_engineerId.ToString(System.Globalization.CultureInfo.InvariantCulture), (string?)await cmd.ExecuteScalarAsync());
    }
}
