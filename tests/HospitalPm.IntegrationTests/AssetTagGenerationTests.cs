using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using HospitalPm.Domain.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>A new machine is numbered by the software; a typed tag still works.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class AssetTagGenerationTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "AssetTag2026!";

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
            var room = new Location { Code = $"AT-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
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
                UserName = $"at-{_suffix}", FullName = "Tag Admin", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, Domain.Identity.Roles.BmeHead);
        }

        var login = await _client.PostAsJsonAsync("/api/auth/login", new { userName = $"at-{_suffix}", password = Password });
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

    private async Task<string> CreateWithoutTagAsync()
    {
        var created = await _client.PostAsJsonAsync("/api/equipment", new { equipmentTypeId = _typeId, locationId = _roomId });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("assetTag").GetString()!;
    }

    [Fact]
    public async Task A_machine_added_without_a_tag_is_given_one_in_the_series()
    {
        var tag = await CreateWithoutTagAsync();

        Assert.Matches(new Regex(@"^EQ-\d{5,}$"), tag);

        var found = await _client.GetFromJsonAsync<JsonElement>($"/api/equipment/by-tag/{tag}");
        Assert.Equal(tag, found.GetProperty("assetTag").GetString());
    }

    [Fact]
    public async Task Each_generated_tag_is_different_even_when_made_together()
    {
        var tags = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CreateWithoutTagAsync()));

        Assert.Equal(8, tags.Distinct().Count());
    }

    [Fact]
    public async Task A_generated_number_that_is_already_taken_is_skipped()
    {
        var first = await CreateWithoutTagAsync();
        var next = int.Parse(first[3..], System.Globalization.CultureInfo.InvariantCulture) + 1;

        // Someone typed the number the series is about to reach.
        var typed = await _client.PostAsJsonAsync("/api/equipment", new
        {
            assetTag = $"EQ-{next:D5}", equipmentTypeId = _typeId, locationId = _roomId,
        });
        Assert.Equal(HttpStatusCode.Created, typed.StatusCode);

        var after = await CreateWithoutTagAsync();
        Assert.NotEqual($"EQ-{next:D5}", after);
    }

    [Fact]
    public async Task A_tag_typed_by_hand_is_still_used_and_still_unique()
    {
        var tag = $"HAND-{_suffix}";
        var first = await _client.PostAsJsonAsync("/api/equipment", new { assetTag = tag, equipmentTypeId = _typeId, locationId = _roomId });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var again = await _client.PostAsJsonAsync("/api/equipment", new { assetTag = tag, equipmentTypeId = _typeId, locationId = _roomId });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Editing_still_needs_a_tag()
    {
        var tag = await CreateWithoutTagAsync();
        var machine = await _client.GetFromJsonAsync<JsonElement>($"/api/equipment/by-tag/{tag}");
        var id = machine.GetProperty("id").GetInt32();

        var blank = await _client.PutAsJsonAsync($"/api/equipment/{id}", new
        {
            assetTag = "", equipmentTypeId = _typeId, locationId = _roomId,
        });

        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }
}
