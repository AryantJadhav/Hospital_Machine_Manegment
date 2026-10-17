using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Reports;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>The response-time report's counting, with no database.</summary>
public sealed class ResponseTimeReportCountingTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Day = new(2026, 10, 12);

    private static readonly Dictionary<WorkOrderPriority, int> Targets = new()
    {
        [WorkOrderPriority.Critical] = 15,
        [WorkOrderPriority.High] = 60,
        [WorkOrderPriority.Medium] = 240,
        [WorkOrderPriority.Low] = 1440,
    };

    private static ResponseInput Fault(
        int id, WorkOrderPriority priority, int raisedMinutesAgo, int? startedAfterMinutes,
        WorkOrderStatus status = WorkOrderStatus.InProgress, string? who = "Asha", int? userId = 7) =>
        new(id, $"WO-{id}", id, $"TAG-{id}", "Ventilator", "ICU", priority, status,
            Now.AddMinutes(-raisedMinutesAgo),
            startedAfterMinutes is { } s ? Now.AddMinutes(-raisedMinutesAgo + s) : null,
            userId, who);

    private static ResponseTimeData Build(params ResponseInput[] faults) => ResponseTimeReport.Build(faults, Targets, Day, Day, Now);

    [Fact]
    public void Faults_are_sorted_into_on_time_late_waiting_overdue_and_not_judged()
    {
        var report = Build(
            Fault(1, WorkOrderPriority.High, 300, 30),                                            // on time (30 of 60)
            Fault(2, WorkOrderPriority.High, 300, 90),                                            // late (90 of 60)
            Fault(3, WorkOrderPriority.Critical, 10, null, WorkOrderStatus.Reported),             // waiting (10 of 15)
            Fault(4, WorkOrderPriority.Critical, 45, null, WorkOrderStatus.Assigned),             // overdue
            Fault(5, WorkOrderPriority.Low, 500, null, WorkOrderStatus.Cancelled));               // not judged

        Assert.Equal(5, report.Raised);
        Assert.Equal(1, report.OnTime);
        Assert.Equal(1, report.Late);
        Assert.Equal(1, report.Waiting);
        Assert.Equal(1, report.Overdue);
        Assert.Equal(1, report.NotJudged);
        Assert.Equal(2, report.Responded);
        Assert.Equal(50.0, report.OnTimePercent);
    }

    [Fact]
    public void Average_median_and_longest_are_of_the_faults_an_engineer_started()
    {
        var report = Build(
            Fault(1, WorkOrderPriority.High, 600, 10),
            Fault(2, WorkOrderPriority.High, 600, 20),
            Fault(3, WorkOrderPriority.High, 600, 90),
            Fault(4, WorkOrderPriority.Critical, 30, null, WorkOrderStatus.Reported));   // not started: not in the figures

        Assert.Equal(40, report.AverageMinutes);   // (10 + 20 + 90) / 3
        Assert.Equal(20, report.MedianMinutes);
        Assert.Equal(90, report.LongestMinutes);
    }

    [Fact]
    public void The_median_of_an_even_number_is_the_middle_pair_averaged_and_of_none_is_nothing()
    {
        Assert.Equal(25, ResponseTimeReport.Median([10, 20, 30, 40]));
        Assert.Equal(15, ResponseTimeReport.Median([10, 20]));
        Assert.Null(ResponseTimeReport.Median([]));
    }

    [Fact]
    public void An_empty_period_has_no_percentage_and_no_average_rather_than_a_zero()
    {
        var report = Build();
        Assert.Equal(0, report.Raised);
        Assert.Null(report.OnTimePercent);
        Assert.Null(report.AverageMinutes);
        Assert.Empty(report.ByPriority);
    }

    [Fact]
    public void Each_priority_is_judged_by_its_own_time_and_only_priorities_with_faults_are_listed_most_urgent_first()
    {
        var report = Build(
            Fault(1, WorkOrderPriority.Low, 900, 600),       // 600 of 1440: on time
            Fault(2, WorkOrderPriority.Critical, 600, 20),   // 20 of 15: late
            Fault(3, WorkOrderPriority.Critical, 600, 5));   // on time

        Assert.Equal(["Critical", "Low"], report.ByPriority.Select(p => p.Priority));

        var critical = report.ByPriority[0];
        Assert.Equal(15, critical.TargetMinutes);
        Assert.Equal(2, critical.Raised);
        Assert.Equal(1, critical.OnTime);
        Assert.Equal(1, critical.Late);
        Assert.Equal(13, critical.AverageMinutes);   // (20 + 5) / 2 = 12.5, rounded
        Assert.Equal(20, critical.LongestMinutes);
    }

    [Fact]
    public void Each_engineer_gets_their_own_count_and_one_with_no_name_is_last()
    {
        var report = Build(
            Fault(1, WorkOrderPriority.High, 600, 10, who: "Asha", userId: 7),
            Fault(2, WorkOrderPriority.High, 600, 90, who: "asha", userId: 7),
            Fault(3, WorkOrderPriority.High, 600, 20, who: "Ravi", userId: 8),
            Fault(4, WorkOrderPriority.High, 600, 30, who: null, userId: null));

        Assert.Equal(["Asha", "Ravi", ResponseTimeReport.NotRecorded], report.People.Select(p => p.Name));
        Assert.Equal(2, report.People[0].Answered);
        Assert.Equal(1, report.People[0].OnTime);
        Assert.Equal(1, report.People[0].Late);
        Assert.Equal(50, report.People[0].AverageMinutes);
    }

    [Fact]
    public void The_csv_has_a_row_for_every_fault_with_the_result_in_words()
    {
        var csv = ResponseTimeReport.ToCsv(Build(
            Fault(1, WorkOrderPriority.High, 300, 30),
            Fault(2, WorkOrderPriority.Critical, 45, null, WorkOrderStatus.Assigned)));

        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Contains(",On time,", lines[2], StringComparison.Ordinal);   // the older fault is listed after the newer
        Assert.Contains(",Overdue,30", lines[1], StringComparison.Ordinal);
    }
}

/// <summary>The response-time report over HTTP, against the real database.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class ResponseTimeReportApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "RespReport2026!";

    private ApiFactory _factory = null!;
    private HttpClient _head = null!;
    private HttpClient _engineer = null!;
    private int _headId;
    private string _suffix = null!;
    private int _typeId;
    private int _roomId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var type = new EquipmentType { Code = $"rr-{_suffix}", Name = $"Report type {_suffix}" };
            var room = new Domain.Locations.Location
            {
                Code = $"RR-{_suffix}", Name = $"Report room {_suffix}", Level = Domain.Locations.LocationLevel.Room,
            };
            db.EquipmentTypes.Add(type);
            db.Locations.Add(room);
            await db.SaveChangesAsync();
            _typeId = type.Id;
            _roomId = room.Id;
        }

        (_head, _headId) = await SignInAsync("rr-head", Roles.BmeHead);
        (_engineer, _) = await SignInAsync("rr-eng", Roles.BmeEngineer);

        // Known targets, whatever an earlier test left.
        var known = new[]
        {
            new { priority = 40, minutes = 15 }, new { priority = 30, minutes = 60 },
            new { priority = 20, minutes = 240 }, new { priority = 10, minutes = 1440 },
        };
        Assert.Equal(HttpStatusCode.OK, (await _head.PutAsJsonAsync("/api/work-orders/response-targets", new { targets = known })).StatusCode);
    }

    private async Task<(HttpClient Client, int UserId)> SignInAsync(string prefix, string role)
    {
        int id;
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = $"{prefix}-{_suffix}", FullName = $"{prefix} person {_suffix}", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, role);
            id = user.Id;
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = $"{prefix}-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (client, id);
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _head?.Dispose();
        _engineer?.Dispose();
        _factory?.Dispose();
    }

    private static string Day(int daysFromToday) =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(330).AddDays(daysFromToday)).ToString("yyyy-MM-dd");

    private async Task AddFaultAsync(string name, WorkOrderPriority priority, DateTime reportedUtc, int? startedAfterMinutes)
    {
        await using var db = fixture.CreateContext();
        var machine = new Domain.Assets.Equipment
        {
            AssetTag = $"RR-{name}-{_suffix}".ToUpperInvariant(), EquipmentTypeId = _typeId, LocationId = _roomId,
        };
        db.Equipment.Add(machine);
        await db.SaveChangesAsync();

        var order = new WorkOrder
        {
            EquipmentId = machine.Id,
            FaultDescription = $"Report test {name}",
            Priority = priority,
            ReportedByUserId = _headId,
            ReportedAtUtc = reportedUtc,
            AssignedToUserId = _headId,
        };
        db.WorkOrders.Add(order);
        await db.SaveChangesAsync();

        if (startedAfterMinutes is { } after)
        {
            order.Status = WorkOrderStatus.InProgress;
            order.StartedAtUtc = reportedUtc.AddMinutes(after);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task The_report_counts_the_faults_raised_in_the_period_and_judges_each_against_its_priority()
    {
        // A window nobody else's tests use: the faults here were raised 700 days ago.
        var raised = DateTime.UtcNow.AddDays(-700);
        await AddFaultAsync("ontime", WorkOrderPriority.High, raised, 30);      // 30 of 60
        await AddFaultAsync("late", WorkOrderPriority.High, raised.AddHours(1), 90);   // 90 of 60
        await AddFaultAsync("never", WorkOrderPriority.Critical, raised.AddHours(2), null);   // long overdue
        await AddFaultAsync("outside", WorkOrderPriority.High, DateTime.UtcNow.AddDays(-300), 5);  // another period

        var report = await _head.GetFromJsonAsync<JsonElement>($"/api/reports/response-time?from={Day(-701)}&to={Day(-699)}");

        Assert.Equal(3, report.GetProperty("raised").GetInt32());
        Assert.Equal(1, report.GetProperty("onTime").GetInt32());
        Assert.Equal(1, report.GetProperty("late").GetInt32());
        Assert.Equal(1, report.GetProperty("overdue").GetInt32());
        Assert.Equal(2, report.GetProperty("responded").GetInt32());
        Assert.Equal(50.0, report.GetProperty("onTimePercent").GetDouble());
        Assert.Equal(60, report.GetProperty("averageMinutes").GetInt32());   // (30 + 90) / 2
        Assert.Equal(90, report.GetProperty("longestMinutes").GetInt32());

        var byPriority = report.GetProperty("byPriority").EnumerateArray().ToDictionary(p => p.GetProperty("priority").GetString()!);
        Assert.Equal(2, byPriority["High"].GetProperty("raised").GetInt32());
        Assert.Equal(60, byPriority["High"].GetProperty("targetMinutes").GetInt32());
        Assert.Equal(1, byPriority["Critical"].GetProperty("overdue").GetInt32());

        var results = report.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("result").GetString()).Order().ToArray();
        Assert.Equal(["Late", "On time", "Overdue"], results);
    }

    [Fact]
    public async Task The_csv_downloads_and_only_those_who_may_see_reports_may_ask()
    {
        await AddFaultAsync("csv", WorkOrderPriority.Medium, DateTime.UtcNow.AddDays(-800), 20);

        var csv = await _head.GetAsync($"/api/reports/response-time/report.csv?from={Day(-801)}&to={Day(-799)}");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.StartsWith("text/csv", csv.Content.Headers.ContentType!.MediaType, StringComparison.Ordinal);
        Assert.Contains($"RR-CSV-{_suffix}".ToUpperInvariant(), await csv.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Forbidden, (await _engineer.GetAsync("/api/reports/response-time")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _engineer.GetAsync("/api/reports/response-time/report.csv")).StatusCode);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/reports/response-time")).StatusCode);
    }

    [Fact]
    public async Task A_period_that_ends_before_it_starts_is_refused()
    {
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _head.GetAsync($"/api/reports/response-time?from={Day(0)}&to={Day(-5)}")).StatusCode);
    }
}
