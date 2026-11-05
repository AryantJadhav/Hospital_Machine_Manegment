using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// A machine that is out of the hospital on a gate pass, lost, or retired cannot be repaired by the department that uses it, so
/// the department is not offered it when it reports a fault and is refused if it asks anyway. The biomedical team can still raise
/// a request on a machine that is out on a gate pass, but not on one that is lost.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class FaultOnUnavailableMachineTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "NoFaultOut2026!";

    private ApiFactory _factory = null!;
    private string _suffix = null!;
    private int _typeId;
    private int _roomId;
    private HttpClient _ward = null!;
    private HttpClient _engineer = null!;
    private HttpClient _head = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        int departmentId;
        await using (var db = fixture.CreateContext())
        {
            var type = new EquipmentType { Code = $"fu-{_suffix}", Name = $"Fault type {_suffix}" };
            var department = new Location { Code = $"FUD-{_suffix}", Name = $"Fault ward {_suffix}", Level = LocationLevel.Department };
            db.EquipmentTypes.Add(type);
            db.Locations.Add(department);
            await db.SaveChangesAsync();

            var room = new Location { Code = $"FUR-{_suffix}", Name = $"Fault room {_suffix}", Level = LocationLevel.Room, ParentId = department.Id };
            db.Locations.Add(room);
            await db.SaveChangesAsync();
            _typeId = type.Id;
            _roomId = room.Id;
            departmentId = department.Id;
        }

        _head = (await SignInAsync("fu-head", Roles.BmeHead)).Client;
        _engineer = (await SignInAsync("fu-eng", Roles.BmeEngineer)).Client;
        int wardId;
        (_ward, wardId) = await SignInAsync("fu-ward", Roles.DepartmentUser);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _head.PutAsJsonAsync($"/api/users/{wardId}/departments", new { locationIds = new[] { departmentId } })).StatusCode);
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
        _ward?.Dispose();
        _engineer?.Dispose();
        _head?.Dispose();
        _factory?.Dispose();
    }

    private async Task<int> MachineAsync(string name, EquipmentStatus status = EquipmentStatus.InService)
    {
        await using var db = fixture.CreateContext();
        var machine = new Domain.Assets.Equipment
        {
            AssetTag = $"FU{name}-{_suffix}".ToUpperInvariant(), EquipmentTypeId = _typeId, LocationId = _roomId, Status = status,
        };
        db.Equipment.Add(machine);
        await db.SaveChangesAsync();
        return machine.Id;
    }

    private Task<HttpResponseMessage> ReportAsync(HttpClient who, int machineId) =>
        who.PostAsJsonAsync("/api/work-orders", new { equipmentId = machineId, faultDescription = "Screen is blank", priority = 20 });

    /// <summary>Sends the machine out on a returnable gate pass; returns the pass id.</summary>
    private async Task<int> SendOutAsync(int machineId)
    {
        var res = await _engineer.PostAsJsonAsync("/api/gate-passes", new
        {
            vendorName = $"Vendor {_suffix}", items = new[] { new { equipmentId = machineId, quantity = 1 } },
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task<string[]> TagsAsync(HttpClient who, string query)
    {
        var page = await who.GetFromJsonAsync<JsonElement>($"/api/equipment?{query}&pageSize=100");
        return page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("assetTag").GetString()!).Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task A_department_is_refused_a_fault_on_a_machine_that_is_out_on_a_gate_pass_until_it_is_back()
    {
        var machine = await MachineAsync("A");
        var pass = await SendOutAsync(machine);

        var refused = await ReportAsync(_ward, machine);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var message = await refused.Content.ReadAsStringAsync();
        Assert.Contains("out of the hospital for repair", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GP-", message, StringComparison.Ordinal);

        // Back in the hospital, it can be reported again.
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/gate-passes/{pass}/return", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await ReportAsync(_ward, machine)).StatusCode);
    }

    [Fact]
    public async Task The_biomedical_team_can_still_raise_a_request_on_a_machine_that_is_out_on_a_gate_pass()
    {
        var machine = await MachineAsync("B");
        await SendOutAsync(machine);

        Assert.Equal(HttpStatusCode.Created, (await ReportAsync(_engineer, machine)).StatusCode);
    }

    [Fact]
    public async Task A_lost_machine_takes_no_fault_from_anyone()
    {
        var machine = await MachineAsync("C");
        var loss = await _engineer.PostAsJsonAsync("/api/losses", new
        {
            equipmentId = machine, kind = 10, description = "Not on its stand at the round",
        });
        Assert.Equal(HttpStatusCode.Created, loss.StatusCode);

        foreach (var who in new[] { _ward, _engineer, _head })
        {
            var res = await ReportAsync(who, machine);
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
            Assert.Contains("recorded as lost", await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_condemned_or_disposed_machine_still_takes_no_fault()
    {
        var condemned = await MachineAsync("D", EquipmentStatus.Condemned);
        var disposed = await MachineAsync("E", EquipmentStatus.Disposed);

        foreach (var who in new[] { _ward, _engineer })
        {
            Assert.Equal(HttpStatusCode.Conflict, (await ReportAsync(who, condemned)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await ReportAsync(who, disposed)).StatusCode);
        }
    }

    [Fact]
    public async Task The_picker_for_a_fault_leaves_out_what_cannot_take_one()
    {
        var fine = await MachineAsync("F");
        var out1 = await MachineAsync("G");
        var lost = await MachineAsync("H");
        var retired = await MachineAsync("I", EquipmentStatus.Condemned);
        await SendOutAsync(out1);
        Assert.Equal(HttpStatusCode.Created, (await _engineer.PostAsJsonAsync("/api/losses", new
        {
            equipmentId = lost, kind = 10, description = "Gone",
        })).StatusCode);

        string Tag(string n) => $"FU{n}-{_suffix}".ToUpperInvariant();
        _ = fine;
        _ = retired;
        var q = $"reportable=true&q={_suffix}";

        // A department is offered only the machine in front of it.
        Assert.Equal([Tag("F")], await TagsAsync(_ward, q));
        // The biomedical team is also offered the one that is out on a gate pass, but never the lost or retired ones.
        Assert.Equal([Tag("F"), Tag("G")], await TagsAsync(_engineer, q));

        // Without the filter, the register lists them all: the department can still see its machine and where it stands.
        Assert.Contains(Tag("G"), await TagsAsync(_ward, $"q={_suffix}"));
    }

    [Fact]
    public async Task A_machines_page_can_ask_whether_a_fault_can_be_reported_and_why_not()
    {
        var machine = await MachineAsync("J");
        await SendOutAsync(machine);

        var ward = await _ward.GetFromJsonAsync<JsonElement>($"/api/equipment/{machine}/fault-availability");
        Assert.False(ward.GetProperty("canReport").GetBoolean());
        Assert.Contains("gate pass", ward.GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("GP-", ward.GetProperty("gatePass").GetString(), StringComparison.Ordinal);

        var engineer = await _engineer.GetFromJsonAsync<JsonElement>($"/api/equipment/{machine}/fault-availability");
        Assert.True(engineer.GetProperty("canReport").GetBoolean());
        Assert.Equal(JsonValueKind.Null, engineer.GetProperty("reason").ValueKind);

        var fine = await MachineAsync("K");
        Assert.True((await _ward.GetFromJsonAsync<JsonElement>($"/api/equipment/{fine}/fault-availability")).GetProperty("canReport").GetBoolean());

        Assert.Equal(HttpStatusCode.NotFound, (await _ward.GetAsync("/api/equipment/2000000000/fault-availability")).StatusCode);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/equipment/{fine}/fault-availability")).StatusCode);
    }
}
