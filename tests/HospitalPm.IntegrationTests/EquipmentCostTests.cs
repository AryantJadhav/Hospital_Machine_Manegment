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
/// What a machine cost, and what its insurance costs, in rupees.
///
/// Both are optional, because an older register often has no figure and a blank is
/// truer than a zero. The cost of insurance follows the insurance itself: a machine
/// that is not insured has none, exactly like the insurer and the expiry date.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class EquipmentCostTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "EquipCost2026!";

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
            var room = new Location { Code = $"CO-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
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
                UserName = $"co-{_suffix}", FullName = "Cost Admin", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, Domain.Identity.Roles.Admin);
        }

        var login = await _client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"co-{_suffix}", password = Password });
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

    private string Tag(string kind) => $"CO-{kind}-{_suffix}";

    private object Body(string tag, decimal? cost, bool? insured = null, decimal? insuranceCost = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["assetTag"] = tag,
            ["equipmentTypeId"] = _typeId,
            ["locationId"] = _roomId,
            ["purchaseCost"] = cost,
            ["insuranceCost"] = insuranceCost,
        };
        if (insured is not null)
        {
            body["isInsured"] = insured;
            if (insured == true)
            {
                body["insuranceProvider"] = "New India Assurance";
                body["insuranceExpiryDate"] = "2027-03-31";
            }
        }

        return body;
    }

    private async Task<int> CreateAsync(string tag, decimal? cost, bool? insured = null, decimal? insuranceCost = null)
    {
        var created = await _client.PostAsJsonAsync("/api/equipment", Body(tag, cost, insured, insuranceCost));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private Task<JsonElement> GetAsync(int id) =>
        _client.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}");

    [Fact]
    public async Task A_machine_with_no_cost_has_none_not_zero()
    {
        var m = await GetAsync(await CreateAsync(Tag("A"), cost: null));

        Assert.Equal(JsonValueKind.Null, m.GetProperty("purchaseCost").ValueKind);
        Assert.Equal(JsonValueKind.Null, m.GetProperty("insuranceCost").ValueKind);
    }

    [Fact]
    public async Task The_cost_of_a_machine_is_kept_and_returned()
    {
        var id = await CreateAsync(Tag("B"), 1234567.5m);

        Assert.Equal(1234567.5m, (await GetAsync(id)).GetProperty("purchaseCost").GetDecimal());

        var equipment = (await _client.GetFromJsonAsync<JsonElement>($"/api/equipment/{id}/history"))
            .GetProperty("equipment");
        Assert.Equal(1234567.5m, equipment.GetProperty("purchaseCost").GetDecimal());
    }

    [Fact]
    public async Task A_free_machine_is_zero_which_is_not_the_same_as_unknown()
    {
        var m = await GetAsync(await CreateAsync(Tag("Z"), 0m));

        Assert.Equal(0m, m.GetProperty("purchaseCost").GetDecimal());
    }

    [Fact]
    public async Task Paise_are_rounded_to_two_places()
    {
        var m = await GetAsync(await CreateAsync(Tag("R"), 1000.456m));

        Assert.Equal(1000.46m, m.GetProperty("purchaseCost").GetDecimal());
    }

    [Theory]
    [InlineData("N1", -1, false)]
    [InlineData("N2", 1000000000000, false)]
    [InlineData("N3", -5, true)]
    public async Task A_negative_or_impossible_amount_is_refused(string kind, decimal amount, bool ofInsurance)
    {
        var body = ofInsurance
            ? Body(Tag(kind), null, insured: true, insuranceCost: amount)
            : Body(Tag(kind), amount);

        var refused = await _client.PostAsJsonAsync("/api/equipment", body);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var error = (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains("cost", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_cost_of_insurance_is_kept_when_the_machine_is_insured()
    {
        var id = await CreateAsync(Tag("I"), 500000m, insured: true, insuranceCost: 12500.75m);

        var m = await GetAsync(id);

        Assert.Equal(500000m, m.GetProperty("purchaseCost").GetDecimal());
        Assert.Equal(12500.75m, m.GetProperty("insuranceCost").GetDecimal());
    }

    [Fact]
    public async Task No_insurance_means_no_cost_of_insurance_even_if_one_is_sent()
    {
        var m = await GetAsync(await CreateAsync(Tag("J"), 500000m, insured: false, insuranceCost: 9999m));

        Assert.Equal(JsonValueKind.Null, m.GetProperty("insuranceCost").ValueKind);
        Assert.Equal(500000m, m.GetProperty("purchaseCost").GetDecimal());
    }

    [Fact]
    public async Task Changing_to_not_insured_clears_the_cost_of_insurance()
    {
        var id = await CreateAsync(Tag("C"), 500000m, insured: true, insuranceCost: 12500m);

        var edited = await _client.PutAsJsonAsync($"/api/equipment/{id}", Body(Tag("C"), 500000m, insured: false));

        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await GetAsync(id)).GetProperty("insuranceCost").ValueKind);
    }

    [Fact]
    public async Task Editing_without_saying_about_insurance_leaves_its_cost_alone()
    {
        var id = await CreateAsync(Tag("D"), 500000m, insured: true, insuranceCost: 12500m);

        var edited = await _client.PutAsJsonAsync($"/api/equipment/{id}", Body(Tag("D"), 600000m));

        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        var m = await GetAsync(id);
        Assert.Equal(600000m, m.GetProperty("purchaseCost").GetDecimal());
        Assert.Equal(12500m, m.GetProperty("insuranceCost").GetDecimal());
    }

    [Theory]
    [InlineData("Q1", "UPDATE equipment SET purchase_cost = -1 WHERE id = @id")]
    [InlineData("Q2", "UPDATE equipment SET is_insured = true, insurance_provider = 'X', insurance_expiry_date = '2027-01-01', insurance_cost = -1 WHERE id = @id")]
    [InlineData("Q3", "UPDATE equipment SET insurance_cost = 100 WHERE id = @id")]
    public async Task The_database_itself_refuses_a_cost_that_makes_no_sense(string kind, string sql)
    {
        var id = await CreateAsync(Tag(kind), cost: null);

        // Straight to the table, past the API: the constraint is the last line
        // of defence for anything that writes without going through it.
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("ck_equipment_costs", ex.ConstraintName);
    }
}
