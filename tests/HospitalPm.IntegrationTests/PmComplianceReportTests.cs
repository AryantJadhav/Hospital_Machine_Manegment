using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The compliance report over HTTP, against a department built for the test.
///
/// Everything asserted is scoped to that department, so the figures are exact
/// even though the suite shares one database.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PmComplianceReportTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "Compliance2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private string _suffix = null!;
    private int _departmentId;

    // A month that has ended, so the figures do not move with the calendar.
    private static readonly DateOnly From = new(2026, 3, 1);
    private static readonly DateOnly To = new(2026, 3, 31);

    private string Query => $"from={From:yyyy-MM-dd}&to={To:yyyy-MM-dd}&locationId={_departmentId}";

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        _admin = await SignedInAsync($"cr-adm-{_suffix}", "Head Of Biomedical", Roles.BmeHead);
        _employee = await SignedInAsync($"cr-emp-{_suffix}", "Ward Technician", Roles.BmeEngineer);

        await SeedAsync();
    }

    private async Task<HttpClient> SignedInAsync(string userName, string fullName, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = userName, FullName = fullName, IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, role);
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    /// <summary>
    /// One department, one room, two machines, and a month of PMs: two done on
    /// time (one with a failed check), one done late, one overdue, one skipped,
    /// and one not due until well after the period.
    /// </summary>
    private async Task SeedAsync()
    {
        await using var db = fixture.CreateContext();

        var department = new Location
        {
            Code = $"CRD-{_suffix}", Name = $"Cardiology {_suffix}", Level = LocationLevel.Department,
        };
        db.Locations.Add(department);
        await db.SaveChangesAsync();
        _departmentId = department.Id;

        var room = new Location
        {
            Code = $"CRR-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room, ParentId = department.Id,
        };
        db.Locations.Add(room);
        await db.SaveChangesAsync();

        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
        var one = new Domain.Assets.Equipment
        {
            AssetTag = $"CR1-{_suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
        };
        var two = new Domain.Assets.Equipment
        {
            AssetTag = $"CR2-{_suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
        };
        db.Equipment.AddRange(one, two);

        var template = new ChecklistTemplate { EquipmentTypeId = type.Id, Code = $"cr-{_suffix}", Name = "Compliance PM" };
        db.ChecklistTemplates.Add(template);
        await db.SaveChangesAsync();

        var version = new ChecklistTemplateVersion
        {
            ChecklistTemplateId = template.Id,
            VersionNo = 1,
            Status = ChecklistVersionStatus.Published,
            PublishedAtUtc = DateTime.UtcNow,
            Definition = new ChecklistDefinition
            {
                Sections = [new ChecklistSection
                {
                    Title = "Safety",
                    Items = [new ChecklistItem { Key = "alarm", Label = "Alarm test" }],
                }],
            },
        };
        db.ChecklistTemplateVersions.Add(version);
        await db.SaveChangesAsync();

        // Grace of two days on both schedules.
        var scheduleOne = new PmSchedule
        {
            EquipmentId = one.Id, ChecklistTemplateId = template.Id, Frequency = PmFrequency.Monthly,
            AnchorDate = From, GraceDays = 2,
        };
        var scheduleTwo = new PmSchedule
        {
            EquipmentId = two.Id, ChecklistTemplateId = template.Id, Frequency = PmFrequency.Monthly,
            AnchorDate = From, GraceDays = 2,
        };
        db.PmSchedules.AddRange(scheduleOne, scheduleTwo);
        await db.SaveChangesAsync();

        var signer = await db.Users.FirstAsync(u => u.UserName == $"cr-emp-{_suffix}");

        // Machine one: on time (3 Mar, done 4 Mar within grace) with a failed
        // check, and late (10 Mar, done 20 Mar).
        var onTime = Completed(scheduleOne, one, new DateOnly(2026, 3, 3), Day(2026, 3, 4, 10), version.Id, signer.Id);
        var late = Completed(scheduleOne, one, new DateOnly(2026, 3, 10), Day(2026, 3, 20, 10), version.Id, signer.Id);

        // Machine two: on time (5 Mar), overdue (12 Mar, never done), skipped
        // (18 Mar), and one due in June, after the period.
        var alsoOnTime = Completed(scheduleTwo, two, new DateOnly(2026, 3, 5), Day(2026, 3, 5, 10), version.Id, signer.Id);
        var overdue = Open(scheduleTwo, two, new DateOnly(2026, 3, 12), PmTaskStatus.Overdue);
        var skipped = Open(scheduleTwo, two, new DateOnly(2026, 3, 18), PmTaskStatus.Skipped);
        skipped.SkipReason = "Machine away at the manufacturer";
        var future = Open(scheduleTwo, two, new DateOnly(2026, 6, 1), PmTaskStatus.Scheduled);

        db.PmTasks.AddRange(onTime, late, alsoOnTime, overdue, skipped, future);
        await db.SaveChangesAsync();

        db.PmCompletions.AddRange(
            Completion(onTime, version.Id, signer.Id, new ChecklistAnswer { Value = "fail" }),
            Completion(late, version.Id, signer.Id, new ChecklistAnswer { Value = "pass" }),
            Completion(alsoOnTime, version.Id, signer.Id, new ChecklistAnswer { Value = "pass" }));
        await db.SaveChangesAsync();
    }

    private static DateTime Day(int y, int m, int d, int utcHour) => new(y, m, d, utcHour, 0, 0, DateTimeKind.Utc);

    private static PmTask Completed(
        PmSchedule s, Domain.Assets.Equipment e, DateOnly due, DateTime at, int versionId, int userId) => new()
    {
        PmScheduleId = s.Id, EquipmentId = e.Id, DueDate = due, Status = PmTaskStatus.Completed,
        CompletedAtUtc = at, CompletedByUserId = userId, ChecklistTemplateVersionId = versionId,
    };

    private static PmTask Open(PmSchedule s, Domain.Assets.Equipment e, DateOnly due, PmTaskStatus status) => new()
    {
        PmScheduleId = s.Id, EquipmentId = e.Id, DueDate = due, Status = status,
    };

    private static PmCompletion Completion(PmTask task, int versionId, int userId, ChecklistAnswer answer) => new()
    {
        PmTaskId = task.Id,
        ChecklistTemplateVersionId = versionId,
        Answers = new Dictionary<string, ChecklistAnswer> { ["alarm"] = answer },
        CompletedByUserId = userId,
        CompletedAtUtc = task.CompletedAtUtc!.Value,
        PerformedAtUtc = task.CompletedAtUtc,
        SignedByName = "Ward Technician",
    };

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

    // --- Who may ask ------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("/report.pdf")]
    [InlineData("/report.csv")]
    public async Task An_employee_cannot_run_the_report(string path)
    {
        var response = await _employee.GetAsync($"/api/reports/pm-compliance{path}?{Query}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Nobody_signed_out_can_run_it()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync($"/api/reports/pm-compliance?{Query}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- The figures ------------------------------------------------------------

    [Fact]
    public async Task The_summary_counts_what_fell_due_and_what_became_of_it()
    {
        var report = await _admin.GetFromJsonAsync<JsonElement>($"/api/reports/pm-compliance?{Query}");
        var totals = report.GetProperty("totals");

        // Five PMs fell due in March. The sixth, due in June, is outside the period.
        Assert.Equal(5, totals.GetProperty("due").GetInt32());
        Assert.Equal(2, totals.GetProperty("onTime").GetInt32());
        Assert.Equal(1, totals.GetProperty("late").GetInt32());
        Assert.Equal(1, totals.GetProperty("overdue").GetInt32());
        Assert.Equal(1, totals.GetProperty("skipped").GetInt32());
        Assert.Equal(1, totals.GetProperty("withFindings").GetInt32());

        Assert.Equal(40.0, totals.GetProperty("onSchedulePercent").GetDouble());
        Assert.Equal(60.0, totals.GetProperty("completionPercent").GetDouble());

        // The March period is over, so the June PM is outside it altogether.
        Assert.Equal(0, report.GetProperty("notYetDue").GetInt32());
        Assert.Equal(2, report.GetProperty("machines").GetInt32());
        Assert.Equal(3, report.GetProperty("exceptions").GetInt32());
        Assert.Equal($"Cardiology {_suffix}", report.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Machines_are_grouped_under_their_department_not_their_room()
    {
        var report = await _admin.GetFromJsonAsync<JsonElement>($"/api/reports/pm-compliance?{Query}");
        var groups = report.GetProperty("byDepartment").EnumerateArray().ToArray();

        var only = Assert.Single(groups);
        Assert.Equal($"Cardiology {_suffix}", only.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Leaving_out_the_place_covers_the_whole_hospital()
    {
        var scoped = await _admin.GetFromJsonAsync<JsonElement>($"/api/reports/pm-compliance?{Query}");
        var whole = await _admin.GetFromJsonAsync<JsonElement>(
            $"/api/reports/pm-compliance?from={From:yyyy-MM-dd}&to={To:yyyy-MM-dd}");

        Assert.Equal("Whole hospital", whole.GetProperty("scope").GetString());
        Assert.True(
            whole.GetProperty("totals").GetProperty("due").GetInt32()
                >= scoped.GetProperty("totals").GetProperty("due").GetInt32());
    }

    // --- The documents ------------------------------------------------------------

    [Fact]
    public async Task The_pdf_is_a_pdf_with_a_sensible_name()
    {
        var response = await _admin.GetAsync($"/api/reports/pm-compliance/report.pdf?{Query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("PM-compliance-20260301-20260331.pdf", response.Content.Headers.ContentDisposition?.FileName);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.True(bytes.Length > 2_000);
    }

    [Fact]
    public async Task The_csv_has_one_row_for_every_PM_that_fell_due_and_names_each_outcome()
    {
        var response = await _admin.GetAsync($"/api/reports/pm-compliance/report.csv?{Query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var csv = await response.Content.ReadAsStringAsync();
        var lines = csv.TrimEnd().Split("\r\n");

        Assert.Equal(1 + 5, lines.Length);
        Assert.Contains("Done on time", csv, StringComparison.Ordinal);
        Assert.Contains("Done late", csv, StringComparison.Ordinal);
        Assert.Contains("Overdue, not done", csv, StringComparison.Ordinal);
        Assert.Contains("Skipped", csv, StringComparison.Ordinal);
        Assert.Contains("Machine away at the manufacturer", csv, StringComparison.Ordinal);
        Assert.Contains("Ward Technician", csv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_period_still_produces_a_readable_report()
    {
        var response = await _admin.GetAsync(
            $"/api/reports/pm-compliance/report.pdf?from=2020-01-01&to=2020-01-31&locationId={_departmentId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(await response.Content.ReadAsByteArrayAsync(), 0, 4));
    }

    // --- Bad requests ---------------------------------------------------------------

    [Fact]
    public async Task A_period_that_ends_before_it_starts_is_refused()
    {
        var response = await _admin.GetAsync("/api/reports/pm-compliance?from=2026-03-31&to=2026-03-01");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_absurdly_long_period_is_refused()
    {
        var response = await _admin.GetAsync("/api/reports/pm-compliance?from=1990-01-01&to=2026-03-01");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_place_is_a_404_not_an_empty_report()
    {
        var response = await _admin.GetAsync(
            $"/api/reports/pm-compliance?from={From:yyyy-MM-dd}&to={To:yyyy-MM-dd}&locationId=2000000000");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
