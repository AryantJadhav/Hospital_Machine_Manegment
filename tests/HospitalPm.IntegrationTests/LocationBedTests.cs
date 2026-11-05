using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// A bed is the most exact place a machine can be filed against. Beds are added to a room or a ward a number at a time, and are
/// named with the place they are in, so a machine's place reads "ICU Room 2 · Bed 3" wherever it is shown.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class LocationBedTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "BedNumbers2026!";

    private ApiFactory _factory = null!;
    private string _suffix = null!;
    private HttpClient _head = null!;
    private HttpClient _engineer = null!;
    private HttpClient _ward = null!;
    private int _buildingId;
    private int _departmentId;
    private int _roomId;
    private int _typeId;

    private string RoomName => $"Bed room {_suffix}";

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var type = new EquipmentType { Code = $"bd-{_suffix}", Name = $"Bed type {_suffix}" };
            var building = new Location { Code = $"BDB-{_suffix}", Name = $"Bed block {_suffix}", Level = LocationLevel.Building };
            db.EquipmentTypes.Add(type);
            db.Locations.Add(building);
            await db.SaveChangesAsync();

            var department = new Location { Code = $"BDD-{_suffix}", Name = $"Bed ward {_suffix}", Level = LocationLevel.Department, ParentId = building.Id };
            db.Locations.Add(department);
            await db.SaveChangesAsync();

            var room = new Location { Code = $"BDR-{_suffix}", Name = RoomName, Level = LocationLevel.Room, ParentId = department.Id };
            db.Locations.Add(room);
            await db.SaveChangesAsync();

            _typeId = type.Id;
            _buildingId = building.Id;
            _departmentId = department.Id;
            _roomId = room.Id;
        }

        _head = (await SignInAsync("bd-head", Roles.BmeHead)).Client;
        _engineer = (await SignInAsync("bd-eng", Roles.BmeEngineer)).Client;
        int wardId;
        (_ward, wardId) = await SignInAsync("bd-ward", Roles.DepartmentUser);

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

    private async Task<JsonElement[]> BedsOfAsync(int parentId)
    {
        var all = await _head.GetFromJsonAsync<JsonElement>("/api/locations");
        return all.EnumerateArray()
            .Where(l => l.GetProperty("parentId").ValueKind == JsonValueKind.Number && l.GetProperty("parentId").GetInt32() == parentId
                        && l.GetProperty("level").GetInt32() == (int)LocationLevel.Bed)
            .OrderBy(l => l.GetProperty("code").GetString(), StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public async Task Beds_are_added_to_a_room_numbered_one_after_another_and_named_with_the_room()
    {
        var made = await _head.PostAsJsonAsync($"/api/locations/{_roomId}/beds", new { count = 3 });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        Assert.Equal(3, (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added").GetInt32());

        var beds = await BedsOfAsync(_roomId);
        Assert.Equal([$"{RoomName} · Bed 1", $"{RoomName} · Bed 2", $"{RoomName} · Bed 3"], beds.Select(b => b.GetProperty("name").GetString()));
        Assert.Equal([$"BDR-{_suffix}-B01", $"BDR-{_suffix}-B02", $"BDR-{_suffix}-B03"], beds.Select(b => b.GetProperty("code").GetString()));

        // Adding more carries on from the highest number there already is.
        Assert.Equal(HttpStatusCode.Created, (await _head.PostAsJsonAsync($"/api/locations/{_roomId}/beds", new { count = 2 })).StatusCode);
        var more = await BedsOfAsync(_roomId);
        Assert.Equal(5, more.Length);
        Assert.Equal($"{RoomName} · Bed 5", more[^1].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Beds_can_go_straight_into_a_ward_and_the_numbering_can_start_anywhere()
    {
        Assert.Equal(HttpStatusCode.Created, (await _head.PostAsJsonAsync($"/api/locations/{_departmentId}/beds", new { count = 2, startAt = 11 })).StatusCode);

        var beds = await BedsOfAsync(_departmentId);
        Assert.Equal([$"Bed ward {_suffix} · Bed 11", $"Bed ward {_suffix} · Bed 12"], beds.Select(b => b.GetProperty("name").GetString()));

        // A number that is already used is refused, not silently doubled.
        var taken = await _head.PostAsJsonAsync($"/api/locations/{_departmentId}/beds", new { count = 1, startAt = 12 });
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("already exists", await taken.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Beds_only_go_in_a_room_or_a_ward_and_only_a_few_at_a_time()
    {
        // Not in a building, and not in a place that does not exist.
        var inBuilding = await _head.PostAsJsonAsync($"/api/locations/{_buildingId}/beds", new { count = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, inBuilding.StatusCode);
        Assert.Contains("room or in a ward", await inBuilding.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NotFound, (await _head.PostAsJsonAsync("/api/locations/2000000000/beds", new { count = 1 })).StatusCode);

        foreach (var count in new[] { 0, -1, 201 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await _head.PostAsJsonAsync($"/api/locations/{_roomId}/beds", new { count })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await _head.PostAsJsonAsync($"/api/locations/{_roomId}/beds", new { count = 1, startAt = 0 })).StatusCode);
        Assert.Empty(await BedsOfAsync(_roomId));
    }

    [Fact]
    public async Task Only_those_who_edit_the_places_may_add_beds()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _engineer.PostAsJsonAsync($"/api/locations/{_roomId}/beds", new { count = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _ward.PostAsJsonAsync($"/api/locations/{_roomId}/beds", new { count = 1 })).StatusCode);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/locations/{_roomId}/beds", new { count = 1 })).StatusCode);
    }

    [Fact]
    public async Task A_machine_can_be_filed_against_a_bed_the_wards_people_see_it_and_a_room_with_beds_cannot_be_deleted()
    {
        Assert.Equal(HttpStatusCode.Created, (await _head.PostAsJsonAsync($"/api/locations/{_roomId}/beds", new { count = 2 })).StatusCode);
        var bed = (await BedsOfAsync(_roomId))[0].GetProperty("id").GetInt32();

        await using (var db = fixture.CreateContext())
        {
            db.Equipment.Add(new Domain.Assets.Equipment
            {
                AssetTag = $"BDM-{_suffix}".ToUpperInvariant(), EquipmentTypeId = _typeId, LocationId = bed,
            });
            await db.SaveChangesAsync();
        }

        // The place on the machine's record is the bed, by its full name.
        var asked = await _head.GetFromJsonAsync<JsonElement>($"/api/equipment?q={_suffix}");
        var row = asked.GetProperty("items").EnumerateArray().Single();
        Assert.Equal($"{RoomName} · Bed 1", row.GetProperty("locationName").GetString());

        // A person from the ward sees it: a bed is inside their department.
        var ward = await _ward.GetFromJsonAsync<JsonElement>($"/api/equipment?q={_suffix}");
        Assert.Equal(1, ward.GetProperty("total").GetInt32());

        // Asking for the room, or the ward, finds a machine on one of its beds.
        var byRoom = await _head.GetFromJsonAsync<JsonElement>($"/api/equipment?locationId={_roomId}&q={_suffix}");
        Assert.Equal(1, byRoom.GetProperty("total").GetInt32());

        // A room that has beds in it cannot be deleted from under them.
        Assert.Equal(HttpStatusCode.Conflict, (await _head.DeleteAsync($"/api/locations/{_roomId}")).StatusCode);
    }
}
