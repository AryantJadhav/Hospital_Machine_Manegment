using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>How old a machine is: the counting, with no database.</summary>
public sealed class MachineAgeRuleTests
{
    private static readonly DateOnly Today = new(2026, 10, 12);

    [Fact]
    public void A_month_is_complete_on_the_same_day_number()
    {
        Assert.Equal(1, EquipmentRules.AgeOf(new DateOnly(2026, 9, 12), null, Today)!.Value.Months);
        Assert.Equal(0, EquipmentRules.AgeOf(new DateOnly(2026, 9, 13), null, Today)!.Value.Months);
        Assert.Equal(30, EquipmentRules.AgeOf(new DateOnly(2024, 3, 15), null, Today)!.Value.Months); // 2 years 6 months
        Assert.Equal(24, EquipmentRules.AgeOf(new DateOnly(2024, 10, 12), null, Today)!.Value.Months);
        Assert.Equal(23, EquipmentRules.AgeOf(new DateOnly(2024, 10, 13), null, Today)!.Value.Months);
    }

    [Fact]
    public void Installed_today_is_zero_months_not_nothing()
    {
        var age = EquipmentRules.AgeOf(Today, null, Today);
        Assert.Equal(0, age!.Value.Months);
        Assert.Equal(EquipmentRules.AgeFromInstallation, age.Value.From);
    }

    [Fact]
    public void The_installation_date_wins_and_the_purchase_date_is_the_fallback()
    {
        var both = EquipmentRules.AgeOf(new DateOnly(2025, 10, 12), new DateOnly(2023, 10, 12), Today);
        Assert.Equal(12, both!.Value.Months);
        Assert.Equal(EquipmentRules.AgeFromInstallation, both.Value.From);

        var bought = EquipmentRules.AgeOf(null, new DateOnly(2023, 10, 12), Today);
        Assert.Equal(36, bought!.Value.Months);
        Assert.Equal(EquipmentRules.AgeFromPurchase, bought.Value.From);
    }

    [Fact]
    public void No_date_or_a_date_still_ahead_gives_no_age_rather_than_a_wrong_one()
    {
        Assert.Null(EquipmentRules.AgeOf(null, null, Today));
        Assert.Null(EquipmentRules.AgeOf(Today.AddDays(1), null, Today));
        // Installation still ahead, bought long ago: the machine is not in service yet, so it has no age to show.
        Assert.Null(EquipmentRules.AgeOf(Today.AddDays(30), new DateOnly(2020, 1, 1), Today));
    }

    [Fact]
    public void The_end_of_a_short_month_does_not_count_a_month_early()
    {
        // 31 January to 28 February is not a month; to 1 March it is.
        Assert.Equal(0, EquipmentRules.AgeOf(new DateOnly(2026, 1, 31), null, new DateOnly(2026, 2, 28))!.Value.Months);
        Assert.Equal(1, EquipmentRules.AgeOf(new DateOnly(2026, 1, 31), null, new DateOnly(2026, 3, 1))!.Value.Months);
    }
}

/// <summary>The age as the register and the machine's page are told it, against the real database.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class MachineAgeApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "MachineAge2026!";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _suffix = null!;
    private int _installedId;
    private int _boughtId;
    private int _undatedId;

    // The hospital's day, as the server counts it.
    private static DateOnly HospitalToday => DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(330));

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var type = new EquipmentType { Code = $"age-{_suffix}", Name = $"Age type {_suffix}" };
            var room = new Domain.Locations.Location
            {
                Code = $"AGE-{_suffix}", Name = $"Age room {_suffix}", Level = Domain.Locations.LocationLevel.Room,
            };
            db.EquipmentTypes.Add(type);
            db.Locations.Add(room);
            await db.SaveChangesAsync();

            var installed = new Domain.Assets.Equipment
            {
                AssetTag = $"AGE-I-{_suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
                PurchaseDate = HospitalToday.AddMonths(-40), InstallationDate = HospitalToday.AddMonths(-26),
            };
            var bought = new Domain.Assets.Equipment
            {
                AssetTag = $"AGE-B-{_suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
                PurchaseDate = HospitalToday.AddMonths(-5),
            };
            var undated = new Domain.Assets.Equipment
            {
                AssetTag = $"AGE-U-{_suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
            };
            db.Equipment.AddRange(installed, bought, undated);
            await db.SaveChangesAsync();
            _installedId = installed.Id;
            _boughtId = bought.Id;
            _undatedId = undated.Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = $"age-eng-{_suffix}", FullName = "Age engineer", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, Roles.BmeEngineer);
        }

        _client = _factory.CreateClient();
        var login = await _client.PostAsJsonAsync("/api/auth/login", new { userName = $"age-eng-{_suffix}", password = Password });
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

    [Fact]
    public async Task The_machines_page_says_how_old_it_is_and_from_which_date()
    {
        var installed = await _client.GetFromJsonAsync<JsonElement>($"/api/equipment/{_installedId}");
        Assert.Equal(26, installed.GetProperty("ageMonths").GetInt32());
        Assert.Equal("installation", installed.GetProperty("ageFrom").GetString());

        var bought = await _client.GetFromJsonAsync<JsonElement>($"/api/equipment/{_boughtId}");
        Assert.Equal(5, bought.GetProperty("ageMonths").GetInt32());
        Assert.Equal("purchase", bought.GetProperty("ageFrom").GetString());
    }

    [Fact]
    public async Task A_machine_with_no_date_has_no_age()
    {
        var undated = await _client.GetFromJsonAsync<JsonElement>($"/api/equipment/{_undatedId}");
        Assert.Equal(JsonValueKind.Null, undated.GetProperty("ageMonths").ValueKind);
        Assert.Equal(JsonValueKind.Null, undated.GetProperty("ageFrom").ValueKind);
    }

    [Fact]
    public async Task The_register_list_carries_the_age_too()
    {
        var list = await _client.GetFromJsonAsync<JsonElement>($"/api/equipment?q=AGE-&pageSize=200");
        var byTag = list.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("assetTag").GetString()!.EndsWith(_suffix.ToUpperInvariant(), StringComparison.Ordinal))
            .ToDictionary(i => i.GetProperty("assetTag").GetString()!);

        Assert.Equal(3, byTag.Count);
        Assert.Equal(26, byTag[$"AGE-I-{_suffix}".ToUpperInvariant()].GetProperty("ageMonths").GetInt32());
        Assert.Equal(5, byTag[$"AGE-B-{_suffix}".ToUpperInvariant()].GetProperty("ageMonths").GetInt32());
        Assert.Equal(JsonValueKind.Null, byTag[$"AGE-U-{_suffix}".ToUpperInvariant()].GetProperty("ageMonths").ValueKind);
    }
}
