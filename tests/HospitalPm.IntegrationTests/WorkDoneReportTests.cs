using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Inventory;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>The work-done report's grouping and sums, with no database.</summary>
public sealed class WorkDoneReportBuilderTests
{
    private static readonly DateTime Noon = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

    private static WorkDoneInput Done(
        string number,
        string? by,
        int? byId = null,
        double hoursToFix = 4,
        DateTime? resolved = null,
        params WorkDonePart[] parts) => new(
        WorkOrderId: number.GetHashCode(StringComparison.Ordinal),
        Number: number,
        EquipmentId: 1,
        AssetTag: "BME-1",
        EquipmentType: "Ventilator",
        Location: "ICU",
        Priority: "High",
        Fault: "Alarm sounding",
        Solution: "Replaced the sensor",
        DoneByUserId: byId,
        DoneBy: by,
        ReportedAtUtc: (resolved ?? Noon).AddHours(-hoursToFix),
        ResolvedAtUtc: resolved ?? Noon,
        OutOfServiceAtUtc: null,
        BackInServiceAtUtc: null,
        Parts: parts);

    [Fact]
    public void People_are_ranked_by_how_much_they_fixed_and_those_not_recorded_come_last()
    {
        var report = WorkDoneReport.Build(
            [
                Done("WO-1", "Asha", 1),
                Done("WO-2", null),
                Done("WO-3", null),
                Done("WO-4", "Asha", 1),
                Done("WO-5", "Ravi", 2),
            ],
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 9, 30));

        Assert.Equal(5, report.Done);
        Assert.Equal(["Asha", "Ravi", WorkDoneReport.NotRecorded], report.People.Select(p => p.Name).ToArray());
        Assert.Equal([2, 1, 2], report.People.Select(p => p.Done).ToArray());
    }

    [Fact]
    public void Time_to_fix_is_from_reported_to_resolved_and_is_never_negative()
    {
        var report = WorkDoneReport.Build(
            [
                Done("WO-1", "Asha", hoursToFix: 6),
                Done("WO-2", "Asha", hoursToFix: 2),
                // Resolved before it was reported: a clock set wrong, not a fault fixed in advance.
                Done("WO-3", "Asha", hoursToFix: -3),
            ],
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 9, 30));

        Assert.Equal(0, report.Items.Single(i => i.Number == "WO-3").HoursToFix);
        Assert.Equal(2.67, report.People.Single().AverageHoursToFix, 2);
        Assert.Equal(2.67, report.AverageHoursToFix, 2);
    }

    [Fact]
    public void Parts_cost_adds_up_and_a_part_with_no_cost_is_flagged_not_counted_as_free()
    {
        var report = WorkDoneReport.Build(
            [
                Done("WO-1", "Asha", parts: [new WorkDonePart("P-1", "Filter", 2, 250m), new WorkDonePart("P-2", "Cable", 1, null)]),
                Done("WO-2", "Asha", parts: [new WorkDonePart("P-1", "Filter", 1, 250m)]),
            ],
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 9, 30));

        var first = report.Items.Single(i => i.Number == "WO-1");
        Assert.Equal(500m, first.PartsCost);
        Assert.True(first.PartsCostMissing);
        Assert.False(report.Items.Single(i => i.Number == "WO-2").PartsCostMissing);
        Assert.Equal(750m, report.PartsCost);
    }

    [Fact]
    public void The_latest_work_is_listed_first()
    {
        var report = WorkDoneReport.Build(
            [
                Done("WO-OLD", "Asha", resolved: Noon.AddHours(-5)),
                Done("WO-NEW", "Asha", resolved: Noon),
            ],
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 9, 30));

        Assert.Equal(["WO-NEW", "WO-OLD"], report.Items.Select(i => i.Number).ToArray());
    }

    [Fact]
    public void The_csv_has_a_header_one_row_per_fault_and_the_parts_in_words()
    {
        var report = WorkDoneReport.Build(
            [Done("WO-1", "Asha", parts: [new WorkDonePart("P-1", "Filter", 2, 250m)])],
            new DateOnly(2026, 9, 30),
            new DateOnly(2026, 9, 30));

        var lines = WorkDoneReport.ToCsv(report).TrimEnd().Split("\r\n");

        Assert.Equal(2, lines.Length);
        Assert.StartsWith("﻿Service request,Asset tag", lines[0], StringComparison.Ordinal);
        Assert.Contains("WO-1", lines[1], StringComparison.Ordinal);
        Assert.Contains("2x P-1 Filter", lines[1], StringComparison.Ordinal);
        Assert.Contains("500.00", lines[1], StringComparison.Ordinal);
    }
}

/// <summary>The work-done report over HTTP, against the real database.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class WorkDoneReportApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "WorkDone2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private string _suffix = null!;
    private int _asha;
    private int _ravi;
    private int _deptOne;
    private int _deptTwo;
    private string _todayNumber = null!;
    private string _oldNumber = null!;

    private static string Day(int daysAgo) =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-daysAgo)).ToString("yyyy-MM-dd");

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        int adminId;
        (_admin, adminId) = await SignInAsync("wd-admin", Domain.Identity.Roles.BmeHead);
        (_employee, _) = await SignInAsync("wd-emp", Domain.Identity.Roles.BmeEngineer);
        (_, _asha) = await SignInAsync("wd-asha", Domain.Identity.Roles.BmeEngineer);
        (_, _ravi) = await SignInAsync("wd-ravi", Domain.Identity.Roles.BmeEngineer);

        await using var db = fixture.CreateContext();

        var one = new Location { Code = $"WD1-{_suffix}", Name = $"Work one {_suffix}", Level = LocationLevel.Department };
        var two = new Location { Code = $"WD2-{_suffix}", Name = $"Work two {_suffix}", Level = LocationLevel.Department };
        db.Locations.AddRange(one, two);
        await db.SaveChangesAsync();
        _deptOne = one.Id;
        _deptTwo = two.Id;

        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
        var machineOne = new Equipment { AssetTag = $"WDA-{_suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = one.Id };
        var machineTwo = new Equipment { AssetTag = $"WDB-{_suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = two.Id };
        db.Equipment.AddRange(machineOne, machineTwo);

        var part = new SparePart { PartNumber = $"WDP-{_suffix}", Name = "Flow sensor", QuantityOnHand = 50, ReorderLevel = 1 };
        db.SpareParts.Add(part);
        await db.SaveChangesAsync();

        // Fixed a moment ago by Asha, in department one: it used two parts at 300 each.
        var today = await ResolvedAsync(db, machineOne, adminId, _asha, DateTime.UtcNow.AddSeconds(-2), "Replaced the flow sensor");
        db.WorkOrderParts.Add(new WorkOrderPart
        {
            WorkOrderId = today.Id, SparePartId = part.Id, QuantityUsed = 2, UnitCostAtUse = 300m,
            UsedByUserId = _asha, UsedAtUtc = DateTime.UtcNow,
        });

        // Fixed ten days ago by Ravi, in department two.
        var old = await ResolvedAsync(db, machineTwo, adminId, _ravi, DateTime.UtcNow.AddDays(-10), "Recalibrated the display");
        await db.SaveChangesAsync();

        // The number is given by the database, so it is read back rather than assumed.
        _todayNumber = await db.WorkOrders.AsNoTracking().Where(w => w.Id == today.Id).Select(w => w.Number).SingleAsync();
        _oldNumber = await db.WorkOrders.AsNoTracking().Where(w => w.Id == old.Id).Select(w => w.Number).SingleAsync();
    }

    private static async Task<WorkOrder> ResolvedAsync(
        Infrastructure.Persistence.HospitalPmDbContext db,
        Equipment machine,
        int reportedBy,
        int resolvedBy,
        DateTime resolvedAt,
        string solution)
    {
        var order = new WorkOrder
        {
            EquipmentId = machine.Id,
            FaultDescription = "Fails self test on power up",
            Priority = WorkOrderPriority.High,
            ReportedByUserId = reportedBy,
            ReportedAtUtc = resolvedAt.AddHours(-5),
            OutOfServiceAtUtc = resolvedAt.AddHours(-5),
        };
        db.WorkOrders.Add(order);
        await db.SaveChangesAsync();

        order.Status = WorkOrderStatus.InProgress;
        await db.SaveChangesAsync();

        order.Status = WorkOrderStatus.Resolved;
        order.ResolutionNotes = solution;
        order.ResolvedByUserId = resolvedBy;
        order.ResolvedAtUtc = resolvedAt;
        order.BackInServiceAtUtc = resolvedAt;
        await db.SaveChangesAsync();

        return order;
    }

    private async Task<(HttpClient Client, int UserId)> SignInAsync(string prefix, string role)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
        var user = new Infrastructure.Identity.ApplicationUser
        {
            UserName = $"{prefix}-{_suffix}", FullName = $"{prefix} person {_suffix}", IsActive = true,
        };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        await users.AddToRoleAsync(user, role);
        var userId = user.Id;

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"{prefix}-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (client, userId);
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

    private static HashSet<string> Numbers(JsonElement report) =>
        report.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("number").GetString()!).ToHashSet();

    [Fact]
    public async Task With_no_dates_the_report_is_what_was_done_today()
    {
        var report = await _admin.GetFromJsonAsync<JsonElement>("/api/reports/work-done");
        var numbers = Numbers(report);

        Assert.Contains(_todayNumber, numbers);
        Assert.DoesNotContain(_oldNumber, numbers);
    }

    [Fact]
    public async Task A_wider_period_includes_both_and_says_who_did_each()
    {
        var report = await _admin.GetFromJsonAsync<JsonElement>($"/api/reports/work-done?from={Day(30)}&to={Day(-1)}");

        var items = report.GetProperty("items").EnumerateArray().ToList();
        var today = items.Single(i => i.GetProperty("number").GetString() == _todayNumber);
        var old = items.Single(i => i.GetProperty("number").GetString() == _oldNumber);

        Assert.Equal($"wd-asha person {_suffix}", today.GetProperty("doneBy").GetString());
        Assert.Equal("Replaced the flow sensor", today.GetProperty("solution").GetString());
        Assert.Equal(600m, today.GetProperty("partsCost").GetDecimal());
        Assert.Equal($"wd-ravi person {_suffix}", old.GetProperty("doneBy").GetString());

        var people = report.GetProperty("byPerson").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Contains($"wd-asha person {_suffix}", people);
        Assert.Contains($"wd-ravi person {_suffix}", people);
    }

    [Fact]
    public async Task The_report_can_be_narrowed_to_one_person()
    {
        var report = await _admin.GetFromJsonAsync<JsonElement>(
            $"/api/reports/work-done?from={Day(30)}&to={Day(-1)}&userId={_ravi}");
        var numbers = Numbers(report);

        Assert.Contains(_oldNumber, numbers);
        Assert.DoesNotContain(_todayNumber, numbers);
    }

    [Fact]
    public async Task The_report_can_be_narrowed_to_one_department()
    {
        var report = await _admin.GetFromJsonAsync<JsonElement>(
            $"/api/reports/work-done?from={Day(30)}&to={Day(-1)}&locationId={_deptOne}");
        var numbers = Numbers(report);

        Assert.Contains(_todayNumber, numbers);
        Assert.DoesNotContain(_oldNumber, numbers);
        Assert.NotEqual(_deptOne, _deptTwo);
    }

    [Fact]
    public async Task An_end_before_the_start_is_refused()
    {
        var res = await _admin.GetAsync($"/api/reports/work-done?from={Day(1)}&to={Day(5)}");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task The_csv_downloads_as_a_spreadsheet_file()
    {
        var res = await _admin.GetAsync($"/api/reports/work-done/report.csv?from={Day(30)}&to={Day(-1)}");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType?.MediaType);

        var text = await res.Content.ReadAsStringAsync();
        Assert.Contains(_todayNumber, text, StringComparison.Ordinal);
        Assert.Contains("Replaced the flow sensor", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_an_administrator_can_read_it()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _employee.GetAsync("/api/reports/work-done")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _employee.GetAsync("/api/reports/work-done/report.csv")).StatusCode);
    }
}
