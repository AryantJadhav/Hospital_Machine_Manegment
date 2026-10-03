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

        _admin = await SignInAsync("sp-admin", Domain.Identity.Roles.BmeHead);
        _employee = await SignInAsync("sp-emp", Domain.Identity.Roles.BmeEngineer);
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

    [Theory]
    [InlineData(0, "out", true)]
    [InlineData(1, "low", true)]
    [InlineData(5, "low", true)]
    [InlineData(6, "ok", false)]
    [InlineData(50, "ok", false)]
    public async Task Nothing_left_is_out_of_stock_one_to_five_is_low_and_more_is_fine(int qty, string status, bool isLow)
    {
        var created = await _admin.PostAsJsonAsync("/api/spare-parts", Body($"LEVEL-{qty}", qty: qty));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var seen = await _admin.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{id}");

        Assert.Equal(status, seen.GetProperty("stockStatus").GetString());
        Assert.Equal(isLow, seen.GetProperty("isLow").GetBoolean());
    }

    [Fact]
    public async Task Negative_quantity_or_cost_is_refused()
    {
        var neg1 = await _admin.PostAsJsonAsync("/api/spare-parts",
            new { partNumber = $"NEG-1-{_suffix}", name = "Bad", quantityOnHand = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, neg1.StatusCode);

        var neg3 = await _admin.PostAsJsonAsync("/api/spare-parts",
            new { partNumber = $"NEG-3-{_suffix}", name = "Bad", quantityOnHand = 0, unitCost = -5 });
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

        var low = await _admin.GetFromJsonAsync<JsonElement>($"/api/spare-parts?q={_suffix}&stock=low");
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

    private object WarrantyBody(string number, string? purchaseDate, int? months) => new
    {
        partNumber = $"{number}-{_suffix}",
        name = $"Part {number}",
        quantityOnHand = 10,
        reorderLevel = 5,
        purchaseDate,
        warrantyMonths = months,
    };

    [Fact]
    public async Task A_part_keeps_its_date_of_purchase_and_warranty_and_says_when_it_ends()
    {
        var created = await _admin.PostAsJsonAsync("/api/spare-parts", WarrantyBody("WAR-1", "2026-03-15", 12));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var seen = await _employee.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{id}");

        Assert.Equal("2026-03-15", seen.GetProperty("purchaseDate").GetString());
        Assert.Equal(12, seen.GetProperty("warrantyMonths").GetInt32());
        Assert.Equal("2027-03-15", seen.GetProperty("warrantyExpiryDate").GetString());
    }

    [Fact]
    public async Task A_warranty_that_starts_on_the_31st_ends_on_the_last_day_of_a_shorter_month()
    {
        var created = await _admin.PostAsJsonAsync("/api/spare-parts", WarrantyBody("WAR-2", "2026-08-31", 6));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var seen = await _admin.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{id}");

        // Six months on from 31 August is February, which has no 31st.
        Assert.Equal("2027-02-28", seen.GetProperty("warrantyExpiryDate").GetString());
    }

    [Fact]
    public async Task A_part_with_no_purchase_or_warranty_has_no_end_date()
    {
        var created = await _admin.PostAsJsonAsync("/api/spare-parts", Body("WAR-3"));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var seen = await _admin.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{id}");

        Assert.Equal(JsonValueKind.Null, seen.GetProperty("purchaseDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("warrantyMonths").ValueKind);
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("warrantyExpiryDate").ValueKind);
    }

    [Fact]
    public async Task A_purchase_date_can_stand_alone_without_a_warranty()
    {
        var created = await _admin.PostAsJsonAsync("/api/spare-parts", WarrantyBody("WAR-4", "2026-05-01", null));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var seen = await _admin.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{id}");

        Assert.Equal("2026-05-01", seen.GetProperty("purchaseDate").GetString());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("warrantyExpiryDate").ValueKind);
    }

    [Fact]
    public async Task A_warranty_needs_a_purchase_date_to_run_from()
    {
        var res = await _admin.PostAsJsonAsync("/api/spare-parts", WarrantyBody("WAR-5", null, 12));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("purchase", (await res.Content.ReadAsStringAsync()), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-6)]
    [InlineData(601)]
    public async Task A_warranty_of_no_months_or_an_absurd_length_is_refused(int months)
    {
        var res = await _admin.PostAsJsonAsync("/api/spare-parts", WarrantyBody($"WAR-6-{months}", "2026-03-15", months));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Editing_a_part_changes_and_clears_its_warranty()
    {
        var created = await _admin.PostAsJsonAsync("/api/spare-parts", WarrantyBody("WAR-7", "2026-03-15", 12));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        (await _admin.PutAsJsonAsync($"/api/spare-parts/{id}", WarrantyBody("WAR-7", "2026-04-10", 24)))
            .EnsureSuccessStatusCode();
        var changed = await _admin.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{id}");
        Assert.Equal("2028-04-10", changed.GetProperty("warrantyExpiryDate").GetString());

        (await _admin.PutAsJsonAsync($"/api/spare-parts/{id}", WarrantyBody("WAR-7", null, null)))
            .EnsureSuccessStatusCode();
        var cleared = await _admin.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{id}");
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("purchaseDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("warrantyExpiryDate").ValueKind);
    }

    private async Task<int> AddAsync(string number, int qty, int reorder)
    {
        var created = await _admin.PostAsJsonAsync("/api/spare-parts", Body(number, qty, reorder));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<HashSet<int>> IdsAsync(string stock)
    {
        var list = await _employee.GetFromJsonAsync<JsonElement>($"/api/spare-parts?stock={stock}&pageSize=200&q={_suffix}");
        return list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToHashSet();
    }

    [Fact]
    public async Task The_out_of_stock_filter_lists_only_parts_with_none_left()
    {
        var none = await AddAsync("OUT-NONE", qty: 0, reorder: 5);
        var low = await AddAsync("OUT-LOW", qty: 3, reorder: 5);
        var fine = await AddAsync("OUT-FINE", qty: 20, reorder: 5);

        var ids = await IdsAsync("out");

        Assert.Contains(none, ids);
        Assert.DoesNotContain(low, ids);
        Assert.DoesNotContain(fine, ids);
    }

    [Fact]
    public async Task The_low_stock_filter_lists_parts_running_short_but_not_those_already_out()
    {
        var none = await AddAsync("LOW-NONE", qty: 0, reorder: 5);
        var atLevel = await AddAsync("LOW-AT", qty: 5, reorder: 5);
        var below = await AddAsync("LOW-BELOW", qty: 2, reorder: 5);
        var fine = await AddAsync("LOW-FINE", qty: 6, reorder: 5);

        var ids = await IdsAsync("low");

        Assert.Contains(atLevel, ids);
        Assert.Contains(below, ids);
        // Out is its own list: nothing left is not "low".
        Assert.DoesNotContain(none, ids);
        Assert.DoesNotContain(fine, ids);
    }

    [Fact]
    public async Task One_to_five_is_low_and_six_is_not_whatever_reorder_level_an_old_client_sends()
    {
        var one = await AddAsync("EDGE-ONE", qty: 1, reorder: 0);
        var five = await AddAsync("EDGE-FIVE", qty: 5, reorder: 0);
        var six = await AddAsync("EDGE-SIX", qty: 6, reorder: 100);

        var low = await IdsAsync("low");

        Assert.Contains(one, low);
        Assert.Contains(five, low);
        Assert.DoesNotContain(six, low);
        Assert.DoesNotContain(six, await IdsAsync("out"));
    }

    [Fact]
    public async Task An_unknown_stock_filter_is_refused()
    {
        var res = await _employee.GetAsync("/api/spare-parts?stock=plenty");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task The_database_itself_refuses_a_warranty_with_no_purchase_date()
    {
        await using var db = fixture.CreateContext();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO spare_part (tenant_id, part_number, name, warranty_months) VALUES (1, {0}, 'Bad warranty', 12)",
            $"BAD-{_suffix}"));

        Assert.Equal("ck_spare_part_warranty", ex.ConstraintName);
    }
}
