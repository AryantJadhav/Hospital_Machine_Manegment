using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// A machine is insured or it is not. When it is, the insurer and the date the
/// policy expires are recorded; when it is not, nothing is, and a policy that was
/// recorded before does not linger.
///
/// The server, not the form, is what keeps that true: the details are cleared on
/// "no", refused when they are missing on "yes", and the table has a constraint for
/// anything that writes to it without going through the API.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class EquipmentInsuranceTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "EquipIns2026!";

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
            var room = new Location { Code = $"EI-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
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
                UserName = $"ei-{_suffix}", FullName = "Insurance Admin", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, Domain.Identity.Roles.Admin);
        }

        var login = await _client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"ei-{_suffix}", password = Password });
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

    private string Tag(string kind) => $"EI-{kind}-{_suffix}";

    private object Body(
        string tag, bool? insured, string? provider = null, string? policy = null, string? expires = null,
        string? model = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["assetTag"] = tag,
            ["equipmentTypeId"] = _typeId,
            ["locationId"] = _roomId,
            ["model"] = model,
            ["insuranceProvider"] = provider,
            ["insurancePolicyNumber"] = policy,
            ["insuranceExpiryDate"] = expires,
        };
        if (insured is not null) body["isInsured"] = insured;
        return body;
    }

    private async Task<int> CreateAsync(
        string tag, bool? insured, string? provider = null, string? policy = null, string? expires = null)
    {
        var created = await _client.PostAsJsonAsync("/api/equipment", Body(tag, insured, provider, policy, expires));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private Task<JsonElement> GetAsync(int id) =>
        _client.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}");

    [Fact]
    public async Task A_machine_created_without_saying_is_not_insured()
    {
        var m = await GetAsync(await CreateAsync(Tag("A"), insured: null));

        Assert.False(m.GetProperty("isInsured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("insuranceProvider").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("insuranceExpiryDate").ValueKind);
    }

    [Fact]
    public async Task An_insured_machine_keeps_its_details()
    {
        var id = await CreateAsync(Tag("B"), true, "New India Assurance", "POL-778", "2027-03-31");

        var m = await GetAsync(id);

        Assert.True(m.GetProperty("isInsured").GetBoolean());
        Assert.Equal("New India Assurance", m.GetProperty("insuranceProvider").GetString());
        Assert.Equal("POL-778", m.GetProperty("insurancePolicyNumber").GetString());
        Assert.Equal("2027-03-31", m.GetProperty("insuranceExpiryDate").GetString());
    }

    [Fact]
    public async Task The_machines_page_carries_its_insurance()
    {
        var id = await CreateAsync(Tag("H"), true, "Star Health", null, "2027-01-15");

        var equipment = (await _client.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}/history"))
            .GetProperty("equipment");

        Assert.True(equipment.GetProperty("isInsured").GetBoolean());
        Assert.Equal("Star Health", equipment.GetProperty("insuranceProvider").GetString());
        Assert.Equal("2027-01-15", equipment.GetProperty("insuranceExpiryDate").GetString());
    }

    [Fact]
    public async Task The_policy_number_is_optional()
    {
        var id = await CreateAsync(Tag("P"), true, "Bajaj Allianz", policy: "  ", expires: "2027-06-30");

        var m = await GetAsync(id);

        Assert.True(m.GetProperty("isInsured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("insurancePolicyNumber").ValueKind);
    }

    [Theory]
    [InlineData("R1", null, "2027-03-31", "insurer")]
    [InlineData("R2", "   ", "2027-03-31", "insurer")]
    [InlineData("R3", "New India Assurance", null, "expires")]
    public async Task Yes_needs_an_insurer_and_an_expiry_date(
        string kind, string? provider, string? expires, string mentions)
    {
        var refused = await _client.PostAsJsonAsync("/api/equipment", Body(Tag(kind), true, provider, null, expires));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(mentions, body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task No_keeps_nothing_even_if_details_are_sent()
    {
        var m = await GetAsync(await CreateAsync(Tag("N"), false, "Ignored Ltd", "X-1", "2027-03-31"));

        Assert.False(m.GetProperty("isInsured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("insuranceProvider").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("insurancePolicyNumber").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("insuranceExpiryDate").ValueKind);
    }

    [Fact]
    public async Task Changing_to_no_clears_the_policy()
    {
        var id = await CreateAsync(Tag("C"), true, "New India Assurance", "POL-1", "2027-03-31");

        var edited = await _client.PutAsJsonAsync($"/api/equipment/{id}", Body(Tag("C"), false));

        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        var m = await GetAsync(id);
        Assert.False(m.GetProperty("isInsured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, m.GetProperty("insuranceProvider").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("insuranceExpiryDate").ValueKind);
    }

    [Fact]
    public async Task Editing_without_saying_leaves_the_policy_alone()
    {
        var id = await CreateAsync(Tag("D"), true, "New India Assurance", "POL-2", "2027-03-31");

        var edited = await _client.PutAsJsonAsync(
            $"/api/equipment/{id}", Body(Tag("D"), insured: null, model: "Renamed model"));

        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        var m = await GetAsync(id);
        Assert.True(m.GetProperty("isInsured").GetBoolean());
        Assert.Equal("New India Assurance", m.GetProperty("insuranceProvider").GetString());
        Assert.Equal("2027-03-31", m.GetProperty("insuranceExpiryDate").GetString());
    }

    [Fact]
    public async Task Renewing_a_policy_changes_the_expiry()
    {
        var id = await CreateAsync(Tag("E"), true, "New India Assurance", "POL-3", "2026-03-31");

        var edited = await _client.PutAsJsonAsync(
            $"/api/equipment/{id}", Body(Tag("E"), true, "New India Assurance", "POL-3", "2027-03-31"));

        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        Assert.Equal("2027-03-31", (await GetAsync(id)).GetProperty("insuranceExpiryDate").GetString());
    }

    [Theory]
    [InlineData("Z1", "UPDATE equipment SET is_insured = false, insurance_provider = 'Someone' WHERE id = @id")]
    [InlineData("Z2", "UPDATE equipment SET is_insured = true WHERE id = @id")]
    [InlineData("Z3", "UPDATE equipment SET is_insured = true, insurance_provider = 'Someone' WHERE id = @id")]
    public async Task The_database_itself_refuses_details_that_contradict_the_answer(string kind, string sql)
    {
        var id = await CreateAsync(Tag(kind), insured: null);

        // Straight to the table, past the API: the constraint is the last line
        // of defence for anything that writes without going through it.
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("ck_equipment_insurance", ex.ConstraintName);
    }
}
