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
/// A machine is under an AMC, under a CMC, or under neither.
///
/// Neither is the absence of a type, not a third value. When there is a contract it
/// has a vendor and a period that does not end before it starts; when there is none,
/// nothing about one is stored, and a contract recorded earlier does not linger. The
/// server keeps that true, and the table has a constraint for anything that writes to
/// it without going through the API.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class EquipmentMaintenanceContractTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "EquipAmc2026!";

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
            var room = new Location { Code = $"MC-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
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
                UserName = $"mc-{_suffix}", FullName = "Contract Admin", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, Domain.Identity.Roles.Admin);
        }

        var login = await _client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"mc-{_suffix}", password = Password });
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

    private string Tag(string kind) => $"MC-{kind}-{_suffix}";

    private object Body(
        string tag,
        bool? has,
        MaintenanceContractType? type = MaintenanceContractType.Amc,
        string? vendor = "Philips Healthcare",
        string? number = "CN-100",
        string? starts = "2026-04-01",
        string? ends = "2027-03-31",
        decimal? cost = 45000m,
        string? model = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["assetTag"] = tag,
            ["equipmentTypeId"] = _typeId,
            ["locationId"] = _roomId,
            ["model"] = model,
            ["maintenanceContractType"] = type is null ? null : (int)type,
            ["maintenanceVendor"] = vendor,
            ["maintenanceContractNumber"] = number,
            ["maintenanceStartDate"] = starts,
            ["maintenanceEndDate"] = ends,
            ["maintenanceCost"] = cost,
        };
        if (has is not null) body["hasMaintenanceContract"] = has;
        return body;
    }

    private async Task<int> CreateAsync(string tag, bool? has, MaintenanceContractType? type = MaintenanceContractType.Amc)
    {
        var created = await _client.PostAsJsonAsync("/api/equipment", Body(tag, has, type));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private Task<JsonElement> GetAsync(int id) =>
        _client.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}");

    [Fact]
    public async Task A_machine_created_without_saying_has_no_contract()
    {
        var m = await GetAsync(await CreateAsync(Tag("A"), has: null, type: null));

        Assert.Equal(JsonValueKind.Null, m.GetProperty("maintenanceContractType").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("maintenanceVendor").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("maintenanceEndDate").ValueKind);
    }

    [Theory]
    [InlineData(MaintenanceContractType.Amc)]
    [InlineData(MaintenanceContractType.Cmc)]
    public async Task A_contract_of_either_kind_keeps_its_details(MaintenanceContractType type)
    {
        var id = await CreateAsync(Tag($"K{(int)type}"), true, type);

        var m = await GetAsync(id);

        Assert.Equal((int)type, m.GetProperty("maintenanceContractType").GetInt32());
        Assert.Equal("Philips Healthcare", m.GetProperty("maintenanceVendor").GetString());
        Assert.Equal("CN-100", m.GetProperty("maintenanceContractNumber").GetString());
        Assert.Equal("2026-04-01", m.GetProperty("maintenanceStartDate").GetString());
        Assert.Equal("2027-03-31", m.GetProperty("maintenanceEndDate").GetString());
        Assert.Equal(45000m, m.GetProperty("maintenanceCost").GetDecimal());
    }

    [Fact]
    public async Task The_machines_page_carries_its_contract()
    {
        var id = await CreateAsync(Tag("H"), true, MaintenanceContractType.Cmc);

        var equipment = (await _client.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}/history"))
            .GetProperty("equipment");

        Assert.Equal((int)MaintenanceContractType.Cmc, equipment.GetProperty("maintenanceContractType").GetInt32());
        Assert.Equal("Philips Healthcare", equipment.GetProperty("maintenanceVendor").GetString());
        Assert.Equal("2027-03-31", equipment.GetProperty("maintenanceEndDate").GetString());
    }

    [Fact]
    public async Task The_number_and_the_cost_are_optional()
    {
        var created = await _client.PostAsJsonAsync(
            "/api/equipment", Body(Tag("O"), true, number: "  ", cost: null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var m = await GetAsync(id);

        Assert.Equal(JsonValueKind.Null, m.GetProperty("maintenanceContractNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("maintenanceCost").ValueKind);
    }

    [Theory]
    [InlineData("R1", null, "Philips", "2026-04-01", "2027-03-31", "amc or a cmc")]
    [InlineData("R2", MaintenanceContractType.Amc, null, "2026-04-01", "2027-03-31", "vendor")]
    [InlineData("R3", MaintenanceContractType.Amc, "   ", "2026-04-01", "2027-03-31", "vendor")]
    [InlineData("R4", MaintenanceContractType.Amc, "Philips", null, "2027-03-31", "starts and ends")]
    [InlineData("R5", MaintenanceContractType.Amc, "Philips", "2026-04-01", null, "starts and ends")]
    [InlineData("R6", MaintenanceContractType.Amc, "Philips", "2027-04-01", "2027-03-31", "before it starts")]
    [InlineData("R7", (MaintenanceContractType)99, "Philips", "2026-04-01", "2027-03-31", "amc or a cmc")]
    public async Task A_contract_needs_a_kind_a_vendor_and_a_period_that_makes_sense(
        string kind, MaintenanceContractType? type, string? vendor, string? starts, string? ends, string mentions)
    {
        var refused = await _client.PostAsJsonAsync(
            "/api/equipment", Body(Tag(kind), true, type, vendor, starts: starts, ends: ends));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var error = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains(mentions, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_contract_that_starts_and_ends_on_the_same_day_is_allowed()
    {
        var created = await _client.PostAsJsonAsync(
            "/api/equipment", Body(Tag("S"), true, starts: "2026-05-05", ends: "2026-05-05"));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task No_contract_keeps_nothing_even_if_details_are_sent()
    {
        var created = await _client.PostAsJsonAsync("/api/equipment", Body(Tag("N"), false));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var m = await GetAsync(id);

        Assert.Equal(JsonValueKind.Null, m.GetProperty("maintenanceContractType").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("maintenanceVendor").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("maintenanceCost").ValueKind);
    }

    [Fact]
    public async Task Changing_to_none_clears_the_contract()
    {
        var id = await CreateAsync(Tag("C"), true);

        var edited = await _client.PutAsJsonAsync($"/api/equipment/{id}", Body(Tag("C"), false));

        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        var m = await GetAsync(id);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("maintenanceContractType").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("maintenanceStartDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("maintenanceEndDate").ValueKind);
    }

    [Fact]
    public async Task Editing_without_saying_leaves_the_contract_alone()
    {
        var id = await CreateAsync(Tag("D"), true);

        var edited = await _client.PutAsJsonAsync(
            $"/api/equipment/{id}", Body(Tag("D"), has: null, model: "Renamed model"));

        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        var m = await GetAsync(id);
        Assert.Equal((int)MaintenanceContractType.Amc, m.GetProperty("maintenanceContractType").GetInt32());
        Assert.Equal("2027-03-31", m.GetProperty("maintenanceEndDate").GetString());
    }

    [Fact]
    public async Task Renewing_and_changing_the_kind_is_an_edit()
    {
        var id = await CreateAsync(Tag("E"), true, MaintenanceContractType.Amc);

        var edited = await _client.PutAsJsonAsync(
            $"/api/equipment/{id}",
            Body(Tag("E"), true, MaintenanceContractType.Cmc, starts: "2027-04-01", ends: "2028-03-31"));

        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        var m = await GetAsync(id);
        Assert.Equal((int)MaintenanceContractType.Cmc, m.GetProperty("maintenanceContractType").GetInt32());
        Assert.Equal("2028-03-31", m.GetProperty("maintenanceEndDate").GetString());
    }

    [Fact]
    public async Task A_negative_cost_of_contract_is_refused()
    {
        var refused = await _client.PostAsJsonAsync("/api/equipment", Body(Tag("X"), true, cost: -1m));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Theory]
    [InlineData("Z1", "UPDATE equipment SET maintenance_vendor = 'Someone' WHERE id = @id")]
    [InlineData("Z2", "UPDATE equipment SET maintenance_contract_type = 10 WHERE id = @id")]
    [InlineData("Z3", "UPDATE equipment SET maintenance_contract_type = 30, maintenance_vendor = 'X', maintenance_start_date = '2026-01-01', maintenance_end_date = '2026-12-31' WHERE id = @id")]
    [InlineData("Z4", "UPDATE equipment SET maintenance_contract_type = 10, maintenance_vendor = 'X', maintenance_start_date = '2026-12-31', maintenance_end_date = '2026-01-01' WHERE id = @id")]
    public async Task The_database_itself_refuses_a_contract_that_makes_no_sense(string kind, string sql)
    {
        var id = await CreateAsync(Tag(kind), has: null, type: null);

        // Straight to the table, past the API: the constraint is the last line
        // of defence for anything that writes without going through it.
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("ck_equipment_maintenance_contract", ex.ConstraintName);
    }
}
