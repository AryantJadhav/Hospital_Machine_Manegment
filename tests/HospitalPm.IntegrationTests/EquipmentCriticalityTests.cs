using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// A machine is critical, semi-critical or non-critical, or not yet classified.
///
/// The level is optional on the API because machines that were on a register
/// before it existed, and rows imported from a spreadsheet, have none. The add
/// form makes a person choose one; the server does not, so nothing that already
/// works starts failing.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class EquipmentCriticalityTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "EquipCrit2026!";

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
            var room = new Location { Code = $"EC-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
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
                UserName = $"ec-{_suffix}", FullName = "Criticality Admin", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, Domain.Identity.Roles.Admin);
        }

        var login = await _client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"ec-{_suffix}", password = Password });
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

    private object Body(string tag, int? criticality, string? model = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["assetTag"] = tag,
            ["equipmentTypeId"] = _typeId,
            ["locationId"] = _roomId,
            ["model"] = model,
        };
        if (criticality is not null) body["criticality"] = criticality;
        return body;
    }

    private async Task<int> CreateAsync(string tag, int? criticality)
    {
        var created = await _client.PostAsJsonAsync("/api/equipment", Body(tag, criticality));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> GetAsync(int id) =>
        (await _client.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}")).GetProperty("criticality");

    [Theory]
    [InlineData(EquipmentCriticality.Critical)]
    [InlineData(EquipmentCriticality.SemiCritical)]
    [InlineData(EquipmentCriticality.NonCritical)]
    public async Task A_level_that_is_given_is_kept_and_returned(EquipmentCriticality level)
    {
        var id = await CreateAsync($"EC-K{(int)level}-{_suffix}", (int)level);

        Assert.Equal((int)level, (await GetAsync(id)).GetInt32());
    }

    [Fact]
    public async Task A_machine_created_without_a_level_is_unclassified()
    {
        var id = await CreateAsync($"EC-N-{_suffix}", criticality: null);

        Assert.Equal(JsonValueKind.Null, (await GetAsync(id)).ValueKind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(40)]
    [InlineData(-1)]
    public async Task A_number_that_is_not_a_level_is_refused(int level)
    {
        var refused = await _client.PostAsJsonAsync("/api/equipment", Body($"EC-X{level + 1}-{_suffix}", level));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("criticality", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Editing_a_machine_without_a_level_leaves_its_level_alone()
    {
        var id = await CreateAsync($"EC-D-{_suffix}", (int)EquipmentCriticality.Critical);

        var edited = await _client.PutAsJsonAsync(
            $"/api/equipment/{id}", Body($"EC-D-{_suffix}", criticality: null, model: "Renamed model"));

        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        Assert.Equal((int)EquipmentCriticality.Critical, (await GetAsync(id)).GetInt32());
    }

    [Fact]
    public async Task Editing_a_machine_with_a_level_changes_it()
    {
        var id = await CreateAsync($"EC-E-{_suffix}", (int)EquipmentCriticality.NonCritical);

        var edited = await _client.PutAsJsonAsync(
            $"/api/equipment/{id}", Body($"EC-E-{_suffix}", (int)EquipmentCriticality.SemiCritical));

        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        Assert.Equal((int)EquipmentCriticality.SemiCritical, (await GetAsync(id)).GetInt32());
    }

    [Fact]
    public async Task The_register_can_be_filtered_by_level_and_shows_it()
    {
        await CreateAsync($"EC-F1-{_suffix}", (int)EquipmentCriticality.Critical);
        await CreateAsync($"EC-F2-{_suffix}", (int)EquipmentCriticality.NonCritical);
        await CreateAsync($"EC-F3-{_suffix}", criticality: null);

        var body = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/equipment?q=EC-F&criticality={(int)EquipmentCriticality.Critical}");

        var item = body.GetProperty("items").EnumerateArray().Single(i =>
            i.GetProperty("assetTag").GetString()!.EndsWith(_suffix, StringComparison.Ordinal));
        Assert.Equal($"EC-F1-{_suffix}", item.GetProperty("assetTag").GetString());
        Assert.Equal((int)EquipmentCriticality.Critical, item.GetProperty("criticality").GetInt32());
    }

    [Fact]
    public async Task The_machines_page_carries_its_level()
    {
        var id = await CreateAsync($"EC-H-{_suffix}", (int)EquipmentCriticality.SemiCritical);

        var history = await _client.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}/history");

        Assert.Equal(
            (int)EquipmentCriticality.SemiCritical,
            history.GetProperty("equipment").GetProperty("criticality").GetInt32());
    }

    [Fact]
    public async Task The_database_itself_refuses_a_level_that_is_not_one()
    {
        var id = await CreateAsync($"EC-Z-{_suffix}", criticality: null);

        // Straight to the table, past the API: the constraint is the last line
        // of defence for anything that writes without going through it.
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE equipment SET criticality = 5 WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("ck_equipment_criticality", ex.ConstraintName);
    }
}
