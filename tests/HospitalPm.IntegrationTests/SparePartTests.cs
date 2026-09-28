using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Equipment;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>The spares register: everyone reads it, only an Admin adjusts stock.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class SparePartTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "SparePart2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private string _suffix = null!;
    private int _typeId;
    private int _otherTypeId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var type = new EquipmentType { Code = $"sp-{_suffix}", Name = $"Spares type {_suffix}" };
            var other = new EquipmentType { Code = $"sp2-{_suffix}", Name = $"Other type {_suffix}" };
            db.EquipmentTypes.AddRange(type, other);
            await db.SaveChangesAsync();
            _typeId = type.Id;
            _otherTypeId = other.Id;
        }

        _admin = await SignInAsync("sp-admin", Domain.Identity.Roles.Admin);
        _employee = await SignInAsync("sp-emp", Domain.Identity.Roles.Employee);
    }

    private async Task<HttpClient> SignInAsync(string prefix, string role)
    {
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
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"{prefix}-{_suffix}", password = Password });
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

    private object Body(string number, int qty = 10, int reorder = 5, int? typeId = null) => new
    {
        partNumber = $"{number}-{_suffix}",
        name = $"Part {number}",
        equipmentTypeId = typeId,
        quantityOnHand = qty,
        reorderLevel = reorder,
    };

    [Fact]
    public async Task An_admin_can_add_a_part_and_an_employee_can_read_it()
    {
        var created = await _admin.PostAsJsonAsync("/api/spare-parts", Body("FUSE-1", typeId: _typeId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var seen = await _employee.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{id}");
        Assert.Equal($"FUSE-1-{_suffix}", seen.GetProperty("partNumber").GetString());
        Assert.Equal("Part FUSE-1", seen.GetProperty("name").GetString());
        Assert.Equal($"Spares type {_suffix}", seen.GetProperty("equipmentTypeName").GetString());
        Assert.Equal(10, seen.GetProperty("quantityOnHand").GetInt32());
        Assert.False(seen.GetProperty("isLow").GetBoolean());
        Assert.True(seen.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task An_employee_cannot_add_or_edit_a_part()
    {
        var attempt = await _employee.PostAsJsonAsync("/api/spare-parts", Body("FUSE-2"));
        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);

        var created = await _admin.PostAsJsonAsync("/api/spare-parts", Body("FUSE-3"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var edit = await _employee.PutAsJsonAsync($"/api/spare-parts/{id}", Body("FUSE-3", qty: 999));
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
    }

    [Fact]
    public async Task Two_parts_cannot_share_a_part_number()
    {
        (await _admin.PostAsJsonAsync("/api/spare-parts", Body("DUP-1"))).EnsureSuccessStatusCode();
        var again = await _admin.PostAsJsonAsync("/api/spare-parts", Body("DUP-1"));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Quantity_at_or_below_the_reorder_level_is_flagged_low()
    {
        var atLevel = await _admin.PostAsJsonAsync("/api/spare-parts", Body("LOW-1", qty: 5, reorder: 5));
        var id = (await atLevel.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var seen = await _admin.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{id}");
        Assert.True(seen.GetProperty("isLow").GetBoolean());

        var plenty = await _admin.PostAsJsonAsync("/api/spare-parts", Body("LOW-2", qty: 50, reorder: 5));
        var id2 = (await plenty.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var seen2 = await _admin.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{id2}");
        Assert.False(seen2.GetProperty("isLow").GetBoolean());
    }

    [Fact]
    public async Task Negative_quantity_or_reorder_level_or_cost_is_refused()
    {
        var neg1 = await _admin.PostAsJsonAsync("/api/spare-parts",
            new { partNumber = $"NEG-1-{_suffix}", name = "Bad", quantityOnHand = -1, reorderLevel = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, neg1.StatusCode);

        var neg2 = await _admin.PostAsJsonAsync("/api/spare-parts",
            new { partNumber = $"NEG-2-{_suffix}", name = "Bad", quantityOnHand = 0, reorderLevel = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, neg2.StatusCode);

        var neg3 = await _admin.PostAsJsonAsync("/api/spare-parts",
            new { partNumber = $"NEG-3-{_suffix}", name = "Bad", quantityOnHand = 0, reorderLevel = 0, unitCost = -5 });
        Assert.Equal(HttpStatusCode.BadRequest, neg3.StatusCode);
    }

    [Fact]
    public async Task An_unknown_equipment_type_is_refused()
    {
        var response = await _admin.PostAsJsonAsync("/api/spare-parts", Body("BADTYPE-1", typeId: 99999999));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Editing_adjusts_stock_and_the_part_number_stays_put()
    {
        var created = await _admin.PostAsJsonAsync("/api/spare-parts", Body("ADJ-1", qty: 10));
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetInt32();
        var originalNumber = body.GetProperty("partNumber").GetString();

        var edit = await _admin.PutAsJsonAsync($"/api/spare-parts/{id}", new
        {
            partNumber = "SOMETHING-ELSE", // ignored on update, same as an equipment type's code
            name = "Part ADJ-1",
            quantityOnHand = 3,
            reorderLevel = 5,
        });
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);

        var seen = await _admin.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{id}");
        Assert.Equal(originalNumber, seen.GetProperty("partNumber").GetString());
        Assert.Equal(3, seen.GetProperty("quantityOnHand").GetInt32());
        Assert.True(seen.GetProperty("isLow").GetBoolean());
    }

    [Fact]
    public async Task Retiring_a_part_removes_it_from_the_default_list_but_not_by_id()
    {
        var created = await _admin.PostAsJsonAsync("/api/spare-parts", Body("RET-1"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        (await _admin.PutAsJsonAsync($"/api/spare-parts/{id}", new
        {
            partNumber = "ignored", name = "Part RET-1", quantityOnHand = 10, reorderLevel = 5, isActive = false,
        })).EnsureSuccessStatusCode();

        var list = await _admin.GetFromJsonAsync<JsonElement>($"/api/spare-parts?q=RET-1-{_suffix}");
        Assert.Equal(0, list.GetProperty("items").GetArrayLength());

        var withInactive = await _admin.GetFromJsonAsync<JsonElement>($"/api/spare-parts?q=RET-1-{_suffix}&includeInactive=true");
        Assert.Equal(1, withInactive.GetProperty("items").GetArrayLength());

        var direct = await _admin.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{id}");
        Assert.False(direct.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Searching_and_filtering_to_low_stock_only_works()
    {
        await _admin.PostAsJsonAsync("/api/spare-parts", Body("SRCH-widget", qty: 1, reorder: 5));
        await _admin.PostAsJsonAsync("/api/spare-parts", Body("SRCH-gadget", qty: 50, reorder: 5));

        var search = await _admin.GetFromJsonAsync<JsonElement>($"/api/spare-parts?q=widget-{_suffix}");
        Assert.Equal(1, search.GetProperty("total").GetInt32());

        var low = await _admin.GetFromJsonAsync<JsonElement>($"/api/spare-parts?q={_suffix}&lowStockOnly=true");
        var items = low.GetProperty("items").EnumerateArray().ToList();
        Assert.All(items, i => Assert.True(i.GetProperty("isLow").GetBoolean()));
        Assert.Contains(items, i => i.GetProperty("partNumber").GetString()!.Contains("widget"));
        Assert.DoesNotContain(items, i => i.GetProperty("partNumber").GetString()!.Contains("gadget"));
    }

    [Fact]
    public async Task A_spare_part_is_recorded_in_the_audit_log()
    {
        var created = await _admin.PostAsJsonAsync("/api/spare-parts", Body("AUD-1"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        await using var db = fixture.CreateContext();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE table_name = 'spare_part' AND record_pk = @pk", conn);
        cmd.Parameters.AddWithValue("pk", id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(count > 0);
    }
}
