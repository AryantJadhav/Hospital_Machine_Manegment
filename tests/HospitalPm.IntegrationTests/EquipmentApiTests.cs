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
/// Exercises the endpoints over HTTP rather than the DbContext.
///
/// The list endpoint once projected through a helper method, which EF cannot
/// translate; it materialised rows without joining and threw a
/// NullReferenceException on the null navigation. Every DbContext-level test
/// passed. Only a real request catches that shape of bug.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class EquipmentApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _assetTag = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _client = _factory.CreateClient();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userName = $"api-{suffix}";
        const string password = "ApiTesting2026!";
        _assetTag = $"API-{suffix}".ToUpperInvariant();

        await using var db = fixture.CreateContext();

        // Seed through the database: the bootstrap endpoint closes after the
        // first user, and this collection shares one database.
        var room = new Location
        {
            Code = $"R-{suffix}",
            Name = $"Room {suffix}",
            Level = LocationLevel.Room,
        };
        db.Locations.Add(room);
        await db.SaveChangesAsync();

        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");

        db.Equipment.Add(new Equipment
        {
            AssetTag = _assetTag,
            SerialNumber = $"SN-{suffix}",
            EquipmentTypeId = type.Id,
            LocationId = room.Id,
            Manufacturer = "Philips",
        });
        await db.SaveChangesAsync();

        // A user to authenticate as. Created via the API's own registration
        // path would be circular, so it is inserted with a known hash by
        // going through UserManager in the running host.
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();

        var user = new Infrastructure.Identity.ApplicationUser
        {
            UserName = userName,
            FullName = "API Test User",
        };
        await users.CreateAsync(user, password);
        await users.AddToRoleAsync(user, Domain.Identity.Roles.Admin);

        var login = await _client.PostAsJsonAsync("/api/auth/login", new { userName, password });
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
    public async Task Listing_equipment_returns_joined_type_and_location_names()
    {
        var res = await _client.GetAsync($"/api/equipment?q={_assetTag}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var item = body.GetProperty("items").EnumerateArray().Single();

        // The names come from navigations. If the projection stops being
        // translatable these are null and the request 500s.
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("equipmentTypeName").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("locationName").GetString()));
    }

    [Fact]
    public async Task Asset_tag_lookup_is_case_insensitive()
    {
        // A technician typing a tag off a scratched label will not match the
        // stored casing.
        var res = await _client.GetAsync($"/api/equipment/by-tag/{_assetTag.ToLowerInvariant()}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var item = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(_assetTag, item.GetProperty("assetTag").GetString());
        Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("locationName").GetString()));
    }

    [Fact]
    public async Task Unknown_asset_tag_returns_404_not_500()
    {
        var res = await _client.GetAsync("/api/equipment/by-tag/definitely-not-a-real-tag");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Locations_endpoint_returns_the_tree()
    {
        var res = await _client.GetAsync("/api/locations");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var items = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEmpty(items.EnumerateArray().ToList());
    }

    [Fact]
    public async Task Lookups_are_readable_and_populated()
    {
        var types = await _client.GetFromJsonAsync<JsonElement>("/api/lookups/equipment-types");
        var categories = await _client.GetFromJsonAsync<JsonElement>("/api/lookups/categories");

        Assert.True(types.EnumerateArray().Count() > 100);
        Assert.Equal(18, categories.EnumerateArray().Count());
    }

    [Fact]
    public async Task An_admin_may_condemn_an_asset()
    {
        // Condemning is restricted to Admin. This client is an Admin, so the
        // route must accept it.
        await using var db = fixture.CreateContext();
        var asset = await db.Equipment.AsNoTracking().FirstAsync(e => e.AssetTag == _assetTag);

        var res = await _client.PostAsync($"/api/equipment/{asset.Id}/condemn", null);

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    [Fact]
    public async Task An_admin_may_download_the_import_template()
    {
        // The import group requires Admin. The negative case - an
        // unauthenticated caller - is covered by the CI smoke test.
        var res = await _client.GetAsync("/api/equipment/import/template");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            res.Content.Headers.ContentType?.MediaType);
    }
}
