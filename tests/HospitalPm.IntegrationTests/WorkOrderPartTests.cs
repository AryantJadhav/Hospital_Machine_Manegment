using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Inventory;
using HospitalPm.Domain.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>Drawing a spare against a repair: the shelf and the ticket must agree.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class WorkOrderPartTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "WorkOrderPart2026!";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _suffix = null!;
    private int _workOrderId;
    private int _partId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _client = _factory.CreateClient();
        _suffix = Guid.NewGuid().ToString("N")[..8];

        int equipmentId;
        await using (var db = fixture.CreateContext())
        {
            var room = new Location { Code = $"WOP-{_suffix}", Name = $"Ward {_suffix}", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();

            var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");

            var equipment = new HospitalPm.Domain.Assets.Equipment
            {
                AssetTag = $"WOP-{_suffix}".ToUpperInvariant(),
                EquipmentTypeId = type.Id,
                LocationId = room.Id,
            };
            db.Equipment.Add(equipment);
            await db.SaveChangesAsync();
            equipmentId = equipment.Id;

            var part = new SparePart
            {
                PartNumber = $"WOP-PART-{_suffix}",
                Name = $"Filter {_suffix}",
                QuantityOnHand = 10,
                ReorderLevel = 2,
                UnitCost = 250m,
            };
            db.SpareParts.Add(part);
            await db.SaveChangesAsync();
            _partId = part.Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = $"wop-{_suffix}", FullName = "Work Order Part User", IsActive = true,
            };
            await users.CreateAsync(user, Password);
            await users.AddToRoleAsync(user, Domain.Identity.Roles.Employee);
        }

        var login = await _client.PostAsJsonAsync("/api/auth/login", new { userName = $"wop-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        var created = await _client.PostAsJsonAsync("/api/work-orders", new
        {
            equipmentId,
            faultDescription = "Filter needs replacing",
            priority = 20,
        });
        created.EnsureSuccessStatusCode();
        _workOrderId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
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

    private async Task<int> SparePartQuantityAsync()
    {
        await using var db = fixture.CreateContext();
        return await db.SpareParts.Where(p => p.Id == _partId).Select(p => p.QuantityOnHand).SingleAsync();
    }

    [Fact]
    public async Task Recording_a_part_used_deducts_it_from_the_shelf()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/work-orders/{_workOrderId}/parts", new { sparePartId = _partId, quantityUsed = 3 });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        Assert.Equal(7, await SparePartQuantityAsync());

        var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/work-orders/{_workOrderId}");
        var parts = detail.GetProperty("partsUsed").EnumerateArray().ToList();
        Assert.Single(parts);
        Assert.Equal(3, parts[0].GetProperty("quantityUsed").GetInt32());
        Assert.Equal(250, parts[0].GetProperty("unitCostAtUse").GetDecimal());
        Assert.Equal($"WOP-PART-{_suffix}", parts[0].GetProperty("partNumber").GetString());
    }

    [Fact]
    public async Task Recording_more_than_is_on_the_shelf_is_refused()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/work-orders/{_workOrderId}/parts", new { sparePartId = _partId, quantityUsed = 999 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Refused, so the shelf must not have moved.
        Assert.Equal(10, await SparePartQuantityAsync());
    }

    [Fact]
    public async Task Zero_or_negative_quantity_is_refused()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/work-orders/{_workOrderId}/parts", new { sparePartId = _partId, quantityUsed = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_spare_part_is_refused()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/work-orders/{_workOrderId}/parts", new { sparePartId = 99999999, quantityUsed = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_retired_part_cannot_be_recorded_as_used()
    {
        await using (var db = fixture.CreateContext())
        {
            var part = await db.SpareParts.SingleAsync(p => p.Id == _partId);
            part.IsActive = false;
            await db.SaveChangesAsync();
        }

        var response = await _client.PostAsJsonAsync(
            $"/api/work-orders/{_workOrderId}/parts", new { sparePartId = _partId, quantityUsed = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Removing_a_recorded_part_restocks_it()
    {
        var used = await _client.PostAsJsonAsync(
            $"/api/work-orders/{_workOrderId}/parts", new { sparePartId = _partId, quantityUsed = 4 });
        used.EnsureSuccessStatusCode();
        Assert.Equal(6, await SparePartQuantityAsync());

        var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/work-orders/{_workOrderId}");
        var usageId = detail.GetProperty("partsUsed")[0].GetProperty("id").GetInt32();

        var removed = await _client.DeleteAsync($"/api/work-orders/{_workOrderId}/parts/{usageId}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        Assert.Equal(10, await SparePartQuantityAsync());

        var after = await _client.GetFromJsonAsync<JsonElement>($"/api/work-orders/{_workOrderId}");
        Assert.Empty(after.GetProperty("partsUsed").EnumerateArray());
    }

    [Fact]
    public async Task Parts_cannot_be_recorded_or_removed_once_the_order_is_closed()
    {
        var used = await _client.PostAsJsonAsync(
            $"/api/work-orders/{_workOrderId}/parts", new { sparePartId = _partId, quantityUsed = 2 });
        used.EnsureSuccessStatusCode();
        var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/work-orders/{_workOrderId}");
        var usageId = detail.GetProperty("partsUsed")[0].GetProperty("id").GetInt32();

        (await _client.PostAsJsonAsync($"/api/work-orders/{_workOrderId}/status", new { status = 30 })).EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync($"/api/work-orders/{_workOrderId}/resolve", new { resolutionNotes = "Filter replaced." })).EnsureSuccessStatusCode();
        (await _client.PostAsJsonAsync($"/api/work-orders/{_workOrderId}/status", new { status = 60 })).EnsureSuccessStatusCode();

        var blockedAdd = await _client.PostAsJsonAsync(
            $"/api/work-orders/{_workOrderId}/parts", new { sparePartId = _partId, quantityUsed = 1 });
        Assert.Equal(HttpStatusCode.Conflict, blockedAdd.StatusCode);

        var blockedRemove = await _client.DeleteAsync($"/api/work-orders/{_workOrderId}/parts/{usageId}");
        Assert.Equal(HttpStatusCode.Conflict, blockedRemove.StatusCode);
    }

    [Fact]
    public async Task A_part_used_is_recorded_in_the_audit_log()
    {
        var used = await _client.PostAsJsonAsync(
            $"/api/work-orders/{_workOrderId}/parts", new { sparePartId = _partId, quantityUsed = 1 });
        used.EnsureSuccessStatusCode();
        var detail = await _client.GetFromJsonAsync<JsonElement>($"/api/work-orders/{_workOrderId}");
        var usageId = detail.GetProperty("partsUsed")[0].GetProperty("id").GetInt32();

        await using var db = fixture.CreateContext();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE table_name = 'work_order_part' AND record_pk = @pk", conn);
        cmd.Parameters.AddWithValue("pk", usageId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(count > 0);
    }
}
