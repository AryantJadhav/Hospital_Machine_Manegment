using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>Machines are shifted between rooms; each shift is written down.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class EquipmentMoveTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "EquipMove2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private string _suffix = null!;
    private int _typeId;
    private int _roomA;
    private int _roomB;
    private int _siteId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var site = new Location { Code = $"MS-{_suffix}", Name = $"Site {_suffix}", Level = LocationLevel.Site };
            var a = new Location { Code = $"MA-{_suffix}", Name = $"Room A {_suffix}", Level = LocationLevel.Room };
            var b = new Location { Code = $"MB-{_suffix}", Name = $"Room B {_suffix}", Level = LocationLevel.Room };
            db.Locations.AddRange(site, a, b);
            await db.SaveChangesAsync();
            _siteId = site.Id;
            _roomA = a.Id;
            _roomB = b.Id;
            _typeId = (await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator")).Id;
        }

        _admin = await SignInAsync("mv-admin", Domain.Identity.Roles.BmeHead);
        _employee = await SignInAsync("mv-emp", Domain.Identity.Roles.BmeEngineer);
    }

    private async Task<HttpClient> SignInAsync(string prefix, string role)
    {
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
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"{prefix}-{_suffix}", password = Password });
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

    private async Task<int> CreateMachineAsync(string tag)
    {
        var created = await _admin.PostAsJsonAsync("/api/equipment", new
        {
            assetTag = $"{tag}-{_suffix}", equipmentTypeId = _typeId, locationId = _roomA,
        });
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> MovesAsync(int id)
        => await (await _employee.GetAsync($"/api/equipment/{id}/moves")).Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task An_employee_can_move_a_machine_and_the_move_is_recorded()
    {
        var id = await CreateMachineAsync("MV1");

        var moved = await _employee.PostAsJsonAsync($"/api/equipment/{id}/move", new { toLocationId = _roomB, reason = "Needed in room B" });
        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);

        var machine = await _employee.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}");
        Assert.Equal(_roomB, machine.GetProperty("locationId").GetInt32());

        var moves = await MovesAsync(id);
        Assert.Equal(1, moves.GetArrayLength());
        Assert.Equal($"Room A {_suffix}", moves[0].GetProperty("from").GetString());
        Assert.Equal($"Room B {_suffix}", moves[0].GetProperty("to").GetString());
        Assert.Equal("Needed in room B", moves[0].GetProperty("reason").GetString());
        Assert.Equal("mv-emp person", moves[0].GetProperty("movedBy").GetString());
    }

    [Fact]
    public async Task Moving_to_the_same_place_or_a_site_or_an_unknown_place_is_refused()
    {
        var id = await CreateMachineAsync("MV2");

        Assert.Equal(HttpStatusCode.Conflict,
            (await _employee.PostAsJsonAsync($"/api/equipment/{id}/move", new { toLocationId = _roomA })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _employee.PostAsJsonAsync($"/api/equipment/{id}/move", new { toLocationId = _siteId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _employee.PostAsJsonAsync($"/api/equipment/{id}/move", new { toLocationId = 99999999 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _employee.PostAsJsonAsync("/api/equipment/99999999/move", new { toLocationId = _roomB })).StatusCode);

        Assert.Equal(0, (await MovesAsync(id)).GetArrayLength());
    }

    [Fact]
    public async Task Changing_the_place_on_the_edit_form_is_recorded_as_a_move()
    {
        var id = await CreateMachineAsync("MV3");
        var machine = await _admin.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}");

        var put = await _admin.PutAsJsonAsync($"/api/equipment/{id}", new
        {
            assetTag = machine.GetProperty("assetTag").GetString(),
            equipmentTypeId = _typeId,
            locationId = _roomB,
        });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        Assert.Equal(1, (await MovesAsync(id)).GetArrayLength());

        // Saving again without changing the place adds nothing.
        await _admin.PutAsJsonAsync($"/api/equipment/{id}", new
        {
            assetTag = machine.GetProperty("assetTag").GetString(),
            equipmentTypeId = _typeId,
            locationId = _roomB,
            notes = "again",
        });
        Assert.Equal(1, (await MovesAsync(id)).GetArrayLength());
    }

    [Fact]
    public async Task A_condemned_machine_is_not_moved()
    {
        var id = await CreateMachineAsync("MV4");
        (await _admin.PostAsync($"/api/equipment/{id}/condemn", null)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.Conflict,
            (await _employee.PostAsJsonAsync($"/api/equipment/{id}/move", new { toLocationId = _roomB })).StatusCode);
    }
}
