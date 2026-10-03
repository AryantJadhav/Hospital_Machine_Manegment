using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// An Administrator adds and edits the kinds of machine the hospital keeps.
///
/// A type is a name, an optional description, one or more categories and exactly one
/// primary category. It is never deleted: machines, schedules and checklists hang off
/// it. A type nobody uses any more is switched off and leaves the pickers.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class EquipmentTypeApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "EquipType2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private string _suffix = null!;
    private int _diagnostic;
    private int _imaging;
    private int _monitoring;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            _diagnostic = (await db.Categories.SingleAsync(c => c.Code == "diagnostic")).Id;
            _imaging = (await db.Categories.SingleAsync(c => c.Code == "imaging")).Id;
            _monitoring = (await db.Categories.SingleAsync(c => c.Code == "monitoring")).Id;
        }

        _admin = await SignedInAsync($"et-adm-{_suffix}", Roles.BmeHead);
        _employee = await SignedInAsync($"et-emp-{_suffix}", Roles.BmeEngineer);
    }

    private async Task<HttpClient> SignedInAsync(string userName, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = userName, FullName = userName, IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = Password });
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

    private string Name(string kind) => $"Test {kind} {_suffix}";

    private object Body(string name, int[] categories, int primary, string? description = null, bool? active = null) =>
        new { name, description, categoryIds = categories, primaryCategoryId = primary, isActive = active };

    private async Task<(int Id, string Code)> CreateAsync(string name, int[]? categories = null, int? primary = null)
    {
        var cats = categories ?? [_diagnostic];
        var res = await _admin.PostAsJsonAsync("/api/equipment-types", Body(name, cats, primary ?? cats[0]));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("id").GetInt32(), json.GetProperty("code").GetString()!);
    }

    private async Task<JsonElement> FindAsync(int id)
    {
        var all = await _admin.GetFromJsonAsync<JsonElement>("/api/equipment-types");
        return all.EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == id);
    }

    [Fact]
    public async Task An_employee_cannot_read_or_change_equipment_types()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _employee.GetAsync("/api/equipment-types")).StatusCode);

        var created = await _employee.PostAsJsonAsync(
            "/api/equipment-types", Body(Name("Emp"), [_diagnostic], _diagnostic));
        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
    }

    [Fact]
    public async Task An_administrator_adds_a_type_and_it_appears_in_the_pickers()
    {
        var (id, _) = await CreateAsync(Name("Bedside"), [_diagnostic, _monitoring], _monitoring);

        var type = await FindAsync(id);
        Assert.Equal(Name("Bedside"), type.GetProperty("name").GetString());
        Assert.False(type.GetProperty("isSeeded").GetBoolean());
        Assert.True(type.GetProperty("isActive").GetBoolean());
        Assert.Equal(0, type.GetProperty("machineCount").GetInt32());

        var categories = type.GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal(2, categories.Count);
        // The primary one comes first.
        Assert.True(categories[0].GetProperty("isPrimary").GetBoolean());
        Assert.Equal(_monitoring, categories[0].GetProperty("categoryId").GetInt32());

        // Everyone's picker sees it, primary category included.
        var lookup = await _employee.GetFromJsonAsync<JsonElement>("/api/lookups/equipment-types");
        var found = lookup.EnumerateArray().Single(t => t.GetProperty("id").GetInt32() == id);
        Assert.StartsWith("Monitoring", found.GetProperty("category").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_code_is_made_from_the_name_and_kept_unique()
    {
        var first = await CreateAsync($"Odd  Name / X{_suffix}!");
        Assert.Equal($"odd-name-x{_suffix}", first.Code);

        // The same words with different capitals or punctuation would collide on the code,
        // but not on the name: they are told apart by a number.
        var second = await CreateAsync($"odd name x{_suffix}?");
        Assert.Equal($"odd-name-x{_suffix}-2", second.Code);
    }

    [Fact]
    public async Task Two_types_cannot_share_a_name_whatever_the_case()
    {
        await CreateAsync(Name("Dup"));

        var again = await _admin.PostAsJsonAsync(
            "/api/equipment-types", Body(Name("Dup").ToUpperInvariant(), [_diagnostic], _diagnostic));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Theory]
    [InlineData("", "name")]
    [InlineData("   ", "name")]
    public async Task A_type_needs_a_name(string name, string mentions)
    {
        var res = await _admin.PostAsJsonAsync("/api/equipment-types", Body(name, [_diagnostic], _diagnostic));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains(mentions, (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_type_needs_a_category_and_a_primary_that_is_one_of_them()
    {
        var none = await _admin.PostAsJsonAsync("/api/equipment-types", Body(Name("None"), [], _diagnostic));
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);

        var strayPrimary = await _admin.PostAsJsonAsync(
            "/api/equipment-types", Body(Name("Stray"), [_diagnostic], _imaging));
        Assert.Equal(HttpStatusCode.BadRequest, strayPrimary.StatusCode);

        var unknown = await _admin.PostAsJsonAsync(
            "/api/equipment-types", Body(Name("Unknown"), [2_000_000_000], 2_000_000_000));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Editing_changes_the_name_and_moves_the_primary_category()
    {
        var (id, code) = await CreateAsync(Name("Before"), [_diagnostic, _imaging], _diagnostic);

        var edited = await _admin.PutAsJsonAsync(
            $"/api/equipment-types/{id}", Body(Name("After"), [_diagnostic, _imaging], _imaging, "Now described"));

        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        var type = await FindAsync(id);
        Assert.Equal(Name("After"), type.GetProperty("name").GetString());
        Assert.Equal("Now described", type.GetProperty("description").GetString());
        // Renaming does not change the code: a saved spreadsheet or a checklist may name it.
        Assert.Equal(code, type.GetProperty("code").GetString());

        var categories = type.GetProperty("categories").EnumerateArray().ToList();
        Assert.Equal(2, categories.Count);
        Assert.Equal(_imaging, categories.Single(c => c.GetProperty("isPrimary").GetBoolean())
            .GetProperty("categoryId").GetInt32());
    }

    [Fact]
    public async Task Editing_can_add_and_remove_categories()
    {
        var (id, _) = await CreateAsync(Name("Cats"), [_diagnostic, _imaging], _diagnostic);

        var edited = await _admin.PutAsJsonAsync(
            $"/api/equipment-types/{id}", Body(Name("Cats"), [_imaging, _monitoring], _monitoring));

        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
        var categories = (await FindAsync(id)).GetProperty("categories").EnumerateArray()
            .Select(c => (Id: c.GetProperty("categoryId").GetInt32(), Primary: c.GetProperty("isPrimary").GetBoolean()))
            .OrderBy(c => c.Id).ToList();

        Assert.Equal(
            new[] { (_imaging, false), (_monitoring, true) }.OrderBy(c => c.Item1).ToList(),
            categories.Select(c => (c.Id, c.Primary)).ToList());

        // The database agrees there is exactly one primary.
        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.EquipmentTypeCategories.CountAsync(l => l.EquipmentTypeId == id && l.IsPrimary));
    }

    [Fact]
    public async Task A_seeded_type_can_be_edited_and_stays_seeded_with_its_code()
    {
        await using var db = fixture.CreateContext();
        var ventilator = await db.EquipmentTypes.Include(t => t.Categories)
            .SingleAsync(t => t.Code == "ventilator");
        var primary = ventilator.Categories.Single(c => c.IsPrimary).CategoryId;
        var categoryIds = ventilator.Categories.Select(c => c.CategoryId).ToArray();
        var originalName = ventilator.Name;

        try
        {
            var edited = await _admin.PutAsJsonAsync(
                $"/api/equipment-types/{ventilator.Id}",
                Body($"Ventilator {_suffix}", categoryIds, primary));
            Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);

            var type = await FindAsync(ventilator.Id);
            Assert.Equal("ventilator", type.GetProperty("code").GetString());
            Assert.True(type.GetProperty("isSeeded").GetBoolean());
            Assert.Equal($"Ventilator {_suffix}", type.GetProperty("name").GetString());
        }
        finally
        {
            // The type is shared with every other test in this database.
            await _admin.PutAsJsonAsync(
                $"/api/equipment-types/{ventilator.Id}", Body(originalName, categoryIds, primary));
        }
    }

    [Fact]
    public async Task A_name_cannot_be_changed_to_one_another_type_has()
    {
        var (_, _) = await CreateAsync(Name("Taken"));
        var (id, _) = await CreateAsync(Name("Mine"));

        var edited = await _admin.PutAsJsonAsync(
            $"/api/equipment-types/{id}", Body(Name("Taken"), [_diagnostic], _diagnostic));

        Assert.Equal(HttpStatusCode.Conflict, edited.StatusCode);
    }

    [Fact]
    public async Task Keeping_a_types_own_name_on_edit_is_not_a_clash()
    {
        var (id, _) = await CreateAsync(Name("Same"));

        var edited = await _admin.PutAsJsonAsync(
            $"/api/equipment-types/{id}", Body(Name("Same"), [_diagnostic], _diagnostic, "Just a description"));

        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);
    }

    [Fact]
    public async Task A_type_nobody_uses_can_be_switched_off_and_on()
    {
        var (id, _) = await CreateAsync(Name("Retire"));

        var off = await _admin.PutAsJsonAsync(
            $"/api/equipment-types/{id}", Body(Name("Retire"), [_diagnostic], _diagnostic, active: false));
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        Assert.False((await FindAsync(id)).GetProperty("isActive").GetBoolean());

        // Gone from the pickers, still on the admin's list.
        var lookup = await _employee.GetFromJsonAsync<JsonElement>("/api/lookups/equipment-types");
        Assert.DoesNotContain(lookup.EnumerateArray(), t => t.GetProperty("id").GetInt32() == id);

        var on = await _admin.PutAsJsonAsync(
            $"/api/equipment-types/{id}", Body(Name("Retire"), [_diagnostic], _diagnostic, active: true));
        Assert.Equal(HttpStatusCode.NoContent, on.StatusCode);
        Assert.True((await FindAsync(id)).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task A_type_with_machines_in_use_cannot_be_switched_off_until_they_are_retired()
    {
        var (id, _) = await CreateAsync(Name("Busy"));

        await using (var db = fixture.CreateContext())
        {
            var room = new Location { Code = $"ET-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();
            db.Equipment.Add(new Domain.Assets.Equipment
            {
                AssetTag = $"ET-{_suffix}", EquipmentTypeId = id, LocationId = room.Id,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, (await FindAsync(id)).GetProperty("machineCount").GetInt32());

        var refused = await _admin.PutAsJsonAsync(
            $"/api/equipment-types/{id}", Body(Name("Busy"), [_diagnostic], _diagnostic, active: false));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.True((await FindAsync(id)).GetProperty("isActive").GetBoolean());

        // Retire the machine and the type can go.
        await using (var db = fixture.CreateContext())
        {
            var machine = await db.Equipment.SingleAsync(e => e.AssetTag == $"ET-{_suffix}");
            machine.Status = EquipmentStatus.Condemned;
            await db.SaveChangesAsync();
        }

        var allowed = await _admin.PutAsJsonAsync(
            $"/api/equipment-types/{id}", Body(Name("Busy"), [_diagnostic], _diagnostic, active: false));
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
    }

    [Fact]
    public async Task Editing_a_type_that_does_not_exist_is_a_404()
    {
        var res = await _admin.PutAsJsonAsync(
            "/api/equipment-types/2000000000", Body(Name("Ghost"), [_diagnostic], _diagnostic));

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task A_new_type_can_be_used_for_a_machine()
    {
        var (id, _) = await CreateAsync(Name("Usable"));

        await using var db = fixture.CreateContext();
        var room = new Location { Code = $"EU-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
        db.Locations.Add(room);
        await db.SaveChangesAsync();

        var created = await _admin.PostAsJsonAsync("/api/equipment", new
        {
            assetTag = $"EU-{_suffix}", equipmentTypeId = id, locationId = room.Id,
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }
}
