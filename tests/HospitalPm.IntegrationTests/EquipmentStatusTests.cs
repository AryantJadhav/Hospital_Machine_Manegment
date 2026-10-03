using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// A machine always has one of the five statuses.
///
/// The request declared the status as a plain enum, so a create that left it
/// out arrived as 0 - not a status at all - and was stored as such. The machine
/// then showed no status, matched no status filter, and was skipped by anything
/// that reasons about "in service".
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class EquipmentStatusTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "EquipStatus2026!";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _suffix = null!;
    private int _typeId;
    private int _roomId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _client = _factory.CreateClient();
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var room = new Location { Code = $"ES-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();
            _roomId = room.Id;
            _typeId = (await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator")).Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = $"es-{_suffix}", FullName = "Status Admin", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, Domain.Identity.Roles.BmeHead);
        }

        var login = await _client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"es-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    private object Body(string tag, int? status, string? model = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["assetTag"] = tag,
            ["equipmentTypeId"] = _typeId,
            ["locationId"] = _roomId,
            ["model"] = model,
        };
        if (status is not null) body["status"] = status;
        return body;
    }

    private async Task<int> CreateAsync(string tag, int? status)
    {
        var created = await _client.PostAsJsonAsync("/api/equipment", Body(tag, status));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<int> StatusOfAsync(int id) =>
        (await _client.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}")).GetProperty("status").GetInt32();

    [Fact]
    public async Task A_machine_created_without_a_status_is_in_service()
    {
        var id = await CreateAsync($"ES-A-{_suffix}", status: null);

        Assert.Equal((int)EquipmentStatus.InService, await StatusOfAsync(id));
    }

    [Fact]
    public async Task A_status_that_is_asked_for_is_kept()
    {
        var id = await CreateAsync($"ES-B-{_suffix}", (int)EquipmentStatus.InStore);

        Assert.Equal((int)EquipmentStatus.InStore, await StatusOfAsync(id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(-1)]
    public async Task A_number_that_is_not_a_status_is_refused(int status)
    {
        var refused = await _client.PostAsJsonAsync("/api/equipment", Body($"ES-C{status + 1}-{_suffix}", status));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("status", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Editing_a_machine_without_a_status_leaves_its_status_alone()
    {
        var id = await CreateAsync($"ES-D-{_suffix}", (int)EquipmentStatus.UnderRepair);

        var edited = await _client.PutAsJsonAsync(
            $"/api/equipment/{id}", Body($"ES-D-{_suffix}", status: null, model: "Renamed model"));

        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        Assert.Equal((int)EquipmentStatus.UnderRepair, await StatusOfAsync(id));
        Assert.Equal(
            "Renamed model",
            (await _client.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}")).GetProperty("model").GetString());
    }

    [Fact]
    public async Task Editing_a_machine_with_a_status_changes_it()
    {
        var id = await CreateAsync($"ES-E-{_suffix}", (int)EquipmentStatus.InService);

        var edited = await _client.PutAsJsonAsync(
            $"/api/equipment/{id}", Body($"ES-E-{_suffix}", (int)EquipmentStatus.UnderRepair));

        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        Assert.Equal((int)EquipmentStatus.UnderRepair, await StatusOfAsync(id));
    }
}
