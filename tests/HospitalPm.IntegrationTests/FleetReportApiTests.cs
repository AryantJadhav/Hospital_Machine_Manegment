using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Inventory;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.WorkOrders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The downtime and cost reports over HTTP, against the real database: the projections, the
/// place filter, and that only an Administrator can read them.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class FleetReportApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "FleetReport2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private string _suffix = null!;

    private int _deptOneId;
    private int _deptTwoId;
    private int _idA;
    private string _tagA = null!;
    private string _tagB = null!;
    private string _tagC = null!;

    // A wide period around now, so the windows below sit inside it whatever the hospital's offset.
    private static string From => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)).ToString("yyyy-MM-dd");

    private static string To => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd");

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];
        _tagA = $"FRA-{_suffix}".ToUpperInvariant();
        _tagB = $"FRB-{_suffix}".ToUpperInvariant();
        _tagC = $"FRC-{_suffix}".ToUpperInvariant();

        _admin = await SignInAsync("fr-admin", Domain.Identity.Roles.Admin);
        _employee = await SignInAsync("fr-emp", Domain.Identity.Roles.Employee);

        await using var db = fixture.CreateContext();

        var deptOne = new Location { Code = $"FD1-{_suffix}", Name = $"Fleet one {_suffix}", Level = LocationLevel.Department };
        var deptTwo = new Location { Code = $"FD2-{_suffix}", Name = $"Fleet two {_suffix}", Level = LocationLevel.Department };
        db.Locations.AddRange(deptOne, deptTwo);
        await db.SaveChangesAsync();
        _deptOneId = deptOne.Id;
        _deptTwoId = deptTwo.Id;

        var roomOne = new Location { Code = $"FR1-{_suffix}", Name = $"Room one {_suffix}", Level = LocationLevel.Room, ParentId = deptOne.Id };
        var roomTwo = new Location { Code = $"FR2-{_suffix}", Name = $"Room two {_suffix}", Level = LocationLevel.Room, ParentId = deptTwo.Id };
        db.Locations.AddRange(roomOne, roomTwo);
        await db.SaveChangesAsync();

        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");

        // On the register well before the windows below: a machine is only measured from when it
        // was added, so one created "now" would have no downtime in the past to report.
        var registered = DateTime.UtcNow.AddDays(-60);

        // A: every kind of cost, in department one.
        var a = new Equipment
        {
            AssetTag = _tagA,
            EquipmentTypeId = type.Id,
            LocationId = roomOne.Id,
            CreatedAtUtc = registered,
            PurchaseCost = 100_000m,
            IsInsured = true,
            InsuranceProvider = "Star Health",
            InsuranceExpiryDate = new DateOnly(2030, 1, 1),
            InsuranceCost = 5_000m,
            MaintenanceContractType = MaintenanceContractType.Amc,
            MaintenanceVendor = "Vendor Ltd",
            MaintenanceStartDate = new DateOnly(2026, 1, 1),
            MaintenanceEndDate = new DateOnly(2030, 1, 1),
            MaintenanceCost = 12_000m,
        };

        // B: a purchase cost only, in department two.
        var b = new Equipment
        {
            AssetTag = _tagB, EquipmentTypeId = type.Id, LocationId = roomTwo.Id, PurchaseCost = 40_000m, CreatedAtUtc = registered,
        };

        // C: nothing recorded, in department one.
        var c = new Equipment
        {
            AssetTag = _tagC, EquipmentTypeId = type.Id, LocationId = roomOne.Id, CreatedAtUtc = registered,
        };

        db.Equipment.AddRange(a, b, c);
        await db.SaveChangesAsync();
        _idA = a.Id;

        var userId = (await db.Users.FirstAsync(u => u.UserName == $"fr-admin-{_suffix}")).Id;
        var now = DateTime.UtcNow;

        WorkOrder Order(Equipment e, DateTime from, DateTime? to) => new()
        {
            EquipmentId = e.Id,
            FaultDescription = "Alarm sounding continuously",
            Priority = WorkOrderPriority.High,
            ReportedByUserId = userId,
            ReportedAtUtc = from,
            OutOfServiceAtUtc = from,
            BackInServiceAtUtc = to,
        };

        // A: down for 12 hours two days ago. B: down for 30 hours. C: a report cancelled as raised in error.
        var orderA = Order(a, now.AddDays(-2).Date.AddHours(6), now.AddDays(-2).Date.AddHours(18));
        var orderB = Order(b, now.AddDays(-3).Date, now.AddDays(-3).Date.AddHours(30));
        var orderC = Order(c, now.AddDays(-4).Date, now.AddDays(-4).Date.AddHours(8));
        db.WorkOrders.AddRange(orderA, orderB, orderC);
        await db.SaveChangesAsync();

        orderC.Status = WorkOrderStatus.Cancelled;
        await db.SaveChangesAsync();

        var part = new SparePart { PartNumber = $"FLT-{_suffix}", Name = "Flow sensor", QuantityOnHand = 50, ReorderLevel = 1 };
        db.SpareParts.Add(part);
        await db.SaveChangesAsync();

        WorkOrderPart Used(DateTime at, int qty, decimal? cost) => new()
        {
            WorkOrderId = orderA.Id,
            SparePartId = part.Id,
            QuantityUsed = qty,
            UnitCostAtUse = cost,
            UsedByUserId = userId,
            UsedAtUtc = at,
        };

        db.WorkOrderParts.AddRange(
            Used(now.AddDays(-2), 4, 250m),      // in the period: 1,000
            Used(now.AddDays(-2), 1, null),      // in the period, no cost recorded
            Used(now.AddDays(-90), 3, 999m));    // long before the period: not counted
        await db.SaveChangesAsync();
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

    private string Url(string report, int? location = null, string? from = null, string? to = null)
        => $"/api/reports/{report}?from={from ?? From}&to={to ?? To}" + (location is null ? string.Empty : $"&locationId={location}");

    [Theory]
    [InlineData("downtime")]
    [InlineData("cost")]
    public async Task An_employee_cannot_read_a_fleet_report(string report)
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _employee.GetAsync(Url(report))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _employee.GetAsync(Url($"{report}/report.csv"))).StatusCode);
    }

    [Fact]
    public async Task The_downtime_report_lists_a_machine_with_its_hours_and_leaves_out_a_cancelled_report()
    {
        var body = await _admin.GetFromJsonAsync<JsonElement>(Url("downtime", _deptOneId));

        // Department one holds A (12 hours down) and C (whose only report was cancelled).
        var machine = Assert.Single(body.GetProperty("machines").EnumerateArray());
        Assert.Equal(_tagA, machine.GetProperty("assetTag").GetString());
        Assert.Equal(12, machine.GetProperty("downtimeHours").GetDouble());
        Assert.Equal(1, machine.GetProperty("incidents").GetInt32());
        Assert.False(machine.GetProperty("stillDown").GetBoolean());

        Assert.Equal(1, body.GetProperty("machinesAffected").GetInt32());
        Assert.Equal(12, body.GetProperty("totalDowntimeHours").GetDouble());
    }

    [Fact]
    public async Task The_place_filter_narrows_the_downtime_report_to_that_department()
    {
        var two = await _admin.GetFromJsonAsync<JsonElement>(Url("downtime", _deptTwoId));

        var machine = Assert.Single(two.GetProperty("machines").EnumerateArray());
        Assert.Equal(_tagB, machine.GetProperty("assetTag").GetString());
        Assert.Equal(30, machine.GetProperty("downtimeHours").GetDouble());

        var whole = await _admin.GetFromJsonAsync<JsonElement>(Url("downtime"));
        var tags = whole.GetProperty("machines").EnumerateArray().Select(m => m.GetProperty("assetTag").GetString()).ToList();
        Assert.Contains(_tagA, tags);
        Assert.Contains(_tagB, tags);
        Assert.DoesNotContain(_tagC, tags);
    }

    [Fact]
    public async Task A_period_that_ends_before_it_starts_is_refused()
    {
        var res = await _admin.GetAsync(Url("downtime", from: "2026-09-10", to: "2026-09-01"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task An_unknown_place_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync(Url("downtime", 2_000_000_000))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync(Url("cost", 2_000_000_000))).StatusCode);
    }

    [Fact]
    public async Task The_downtime_csv_downloads_as_a_spreadsheet_with_the_machine_in_it()
    {
        var res = await _admin.GetAsync(Url("downtime/report.csv", _deptOneId));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType?.MediaType);

        var csv = await res.Content.ReadAsStringAsync();
        Assert.Contains(_tagA, csv, StringComparison.Ordinal);
        Assert.DoesNotContain(_tagC, csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cost_report_adds_up_every_kind_of_cost_on_a_machine()
    {
        var body = await _admin.GetFromJsonAsync<JsonElement>(Url("cost", _deptOneId));

        var machine = Assert.Single(body.GetProperty("machines").EnumerateArray());
        Assert.Equal(_tagA, machine.GetProperty("assetTag").GetString());
        Assert.Equal(100_000m, machine.GetProperty("purchaseCost").GetDecimal());
        Assert.Equal(5_000m, machine.GetProperty("insuranceCost").GetDecimal());
        Assert.Equal(12_000m, machine.GetProperty("contractCost").GetDecimal());
        // Only the part used inside the period: 4 x 250. The 3 x 999 from long before it is not counted.
        Assert.Equal(1_000m, machine.GetProperty("partsCost").GetDecimal());
        Assert.Equal(118_000m, machine.GetProperty("total").GetDecimal());

        var totals = body.GetProperty("totals");
        Assert.Equal(118_000m, totals.GetProperty("total").GetDecimal());
        Assert.Equal(1_000m, totals.GetProperty("parts").GetDecimal());
    }

    [Fact]
    public async Task The_cost_report_counts_machines_and_parts_with_no_recorded_cost_rather_than_hiding_them()
    {
        var body = await _admin.GetFromJsonAsync<JsonElement>(Url("cost", _deptOneId));

        // C has no cost of any kind; one part was used without a cost on the shelf's record.
        Assert.Equal(1, body.GetProperty("machinesWithoutCost").GetInt32());
        Assert.Equal(1, body.GetProperty("partsWithoutCost").GetInt32());
    }

    [Fact]
    public async Task The_cost_report_lists_the_parts_used_on_a_machine()
    {
        var body = await _admin.GetFromJsonAsync<JsonElement>(Url("cost", _deptOneId));
        var machine = Assert.Single(body.GetProperty("machines").EnumerateArray());

        // The one part, used twice in the period (4 with a cost, 1 without): five, and only the
        // four priced ones counted, with a flag that the figure is short.
        var part = Assert.Single(machine.GetProperty("parts").EnumerateArray());
        Assert.Equal($"FLT-{_suffix}", part.GetProperty("partNumber").GetString());
        Assert.Equal(5, part.GetProperty("quantity").GetInt32());
        Assert.Equal(1_000m, part.GetProperty("cost").GetDecimal());
        Assert.True(part.GetProperty("costMissing").GetBoolean());
    }

    [Fact]
    public async Task A_renewed_policy_adds_to_the_cost_report()
    {
        // Renewed for a further year at 6,000: the earlier 5,000 policy is kept and both are paid for.
        (await _admin.PostAsJsonAsync($"/api/equipment/{_idA}/insurance/renew", new
        {
            insuranceProvider = "New India",
            insurancePolicyNumber = "POL-2",
            insuranceExpiryDate = "2031-01-01",
            insuranceCost = 6_000,
        })).EnsureSuccessStatusCode();

        var body = await _admin.GetFromJsonAsync<JsonElement>(Url("cost", _deptOneId));
        var machine = Assert.Single(body.GetProperty("machines").EnumerateArray());

        Assert.Equal(11_000m, machine.GetProperty("insuranceCost").GetDecimal());
        Assert.Equal(124_000m, machine.GetProperty("total").GetDecimal());

        var policies = machine.GetProperty("policies").EnumerateArray().ToList();
        Assert.Equal(2, policies.Count);
        Assert.True(policies[0].GetProperty("isCurrent").GetBoolean());
        Assert.Equal("New India", policies[0].GetProperty("provider").GetString());
        Assert.False(policies[1].GetProperty("isCurrent").GetBoolean());
        Assert.Equal("Star Health", policies[1].GetProperty("provider").GetString());
    }

    [Fact]
    public async Task A_machines_own_spend_covers_its_whole_life_and_any_role_can_read_it()
    {
        // No period: the part used long before this report's window counts as well (3 x 999).
        var body = await _employee.GetFromJsonAsync<JsonElement>($"/api/equipment/{_idA}/spend");
        var spend = body.GetProperty("spend");

        Assert.Equal(_tagA, spend.GetProperty("assetTag").GetString());
        Assert.Equal(100_000m, spend.GetProperty("purchaseCost").GetDecimal());
        Assert.Equal(5_000m, spend.GetProperty("insuranceCost").GetDecimal());
        Assert.Equal(12_000m, spend.GetProperty("contractCost").GetDecimal());
        Assert.Equal(3_997m, spend.GetProperty("partsCost").GetDecimal());
        Assert.Equal(120_997m, spend.GetProperty("total").GetDecimal());

        var part = Assert.Single(spend.GetProperty("parts").EnumerateArray());
        Assert.Equal(8, part.GetProperty("quantity").GetInt32());
        Assert.Equal(1, body.GetProperty("partsWithoutCost").GetInt32());
    }

    [Fact]
    public async Task The_spend_of_a_machine_that_does_not_exist_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _employee.GetAsync("/api/equipment/2000000000/spend")).StatusCode);
    }

    [Fact]
    public async Task The_cost_report_place_filter_and_csv_work()
    {
        var two = await _admin.GetFromJsonAsync<JsonElement>(Url("cost", _deptTwoId));
        var machine = Assert.Single(two.GetProperty("machines").EnumerateArray());
        Assert.Equal(_tagB, machine.GetProperty("assetTag").GetString());
        Assert.Equal(40_000m, machine.GetProperty("total").GetDecimal());
        Assert.Equal(JsonValueKind.Null, machine.GetProperty("insuranceCost").ValueKind);

        var csv = await _admin.GetAsync(Url("cost/report.csv", _deptOneId));
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains($"{_tagA},", text, StringComparison.Ordinal);
        Assert.Contains(",118000", text, StringComparison.Ordinal);
    }
}
