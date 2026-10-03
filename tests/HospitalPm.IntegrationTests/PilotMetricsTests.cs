using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Infrastructure.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The weekly pilot numbers.
///
/// The suite shares one database, so nothing here asserts an absolute figure:
/// each test reads the metrics, adds its own rows, reads them again, and
/// asserts on the difference.
///
/// The boundary test exists because weeks and months belong to the hospital
/// but timestamps are stored in UTC. A PM signed at 01:00 on a Monday in India
/// is still Sunday in UTC, and truncating the timestamp to a UTC date counted
/// it in the wrong week.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PilotMetricsTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "PilotMetrics2026!";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _suffix = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _client = _factory.CreateClient();
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await CreateUserAsync($"pilot-{_suffix}", Domain.Identity.Roles.Developer);

        var login = await _client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"pilot-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
    }

    private async Task CreateUserAsync(string userName, string role)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();

        var user = new Infrastructure.Identity.ApplicationUser
        {
            UserName = userName, FullName = userName, IsActive = true,
        };
        var created = await users.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(user, role);
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

    private async Task<JsonElement> MetricsAsync() =>
        await _client.GetFromJsonAsync<JsonElement>("/api/admin/pilot-metrics");

    private static int Pm(JsonElement m, string name) => m.GetProperty("pm").GetProperty(name).GetInt32();

    [Fact]
    public async Task A_completion_lands_in_the_hospitals_week_not_the_utc_week()
    {
        var clock = _factory.Services.GetRequiredService<HospitalClock>();
        var today = clock.Today();
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

        // Local Monday 00:30 - the first half hour of the hospital's week.
        // In UTC that is still Sunday whenever the hospital is ahead of UTC.
        var firstMinutes = weekStart.ToDateTime(new TimeOnly(0, 30), DateTimeKind.Utc) - clock.Offset;
        // Local Sunday 23:30 the week before - the last half hour of the previous one.
        var lastMinutes = weekStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
            - clock.Offset - TimeSpan.FromMinutes(30);

        var before = await MetricsAsync();
        var (schedule, equipment, versionId, signerId) = await SeedAsync();

        await using var db = fixture.CreateContext();
        db.PmTasks.AddRange(
            Done(schedule, equipment, today, versionId, signerId, firstMinutes),
            Done(schedule, equipment, today.AddDays(-1), versionId, signerId, lastMinutes));
        await db.SaveChangesAsync();

        var after = await MetricsAsync();

        // The two land in different weeks, whatever the offset or the day the
        // suite happens to run on. A UTC date truncation puts both in the same
        // one whenever the offset is non-zero.
        Assert.Equal(Pm(before, "completedThisWeek") + 1, Pm(after, "completedThisWeek"));
        Assert.Equal(Pm(before, "completedLastWeek") + 1, Pm(after, "completedLastWeek"));
    }

    /// <summary>
    /// The dashboard and the pilot numbers put a completion in the same month.
    ///
    /// The dashboard used to compare the stored UTC year and month, so a PM
    /// signed at 00:30 on the 1st in India landed in the month before.
    /// </summary>
    [Fact]
    public async Task A_completion_just_after_the_hospitals_month_starts_counts_this_month()
    {
        var clock = _factory.Services.GetRequiredService<HospitalClock>();
        var today = clock.Today();
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        var justInside = monthStart.ToDateTime(new TimeOnly(0, 30), DateTimeKind.Utc) - clock.Offset;
        var justOutside = monthStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)
            - clock.Offset - TimeSpan.FromMinutes(30);

        var dashboardBefore = await DashboardCompletedThisMonthAsync();
        var before = await MetricsAsync();
        var (schedule, equipment, versionId, signerId) = await SeedAsync();

        await using var db = fixture.CreateContext();
        db.PmTasks.AddRange(
            Done(schedule, equipment, monthStart, versionId, signerId, justInside),
            Done(schedule, equipment, monthStart.AddDays(-1), versionId, signerId, justOutside));
        await db.SaveChangesAsync();

        var after = await MetricsAsync();

        Assert.Equal(dashboardBefore + 1, await DashboardCompletedThisMonthAsync());
        Assert.Equal(Pm(before, "completedThisMonth") + 1, Pm(after, "completedThisMonth"));
        Assert.Equal(Pm(before, "completedLastMonth") + 1, Pm(after, "completedLastMonth"));
    }

    private async Task<int> DashboardCompletedThisMonthAsync() =>
        (await _client.GetFromJsonAsync<JsonElement>("/api/dashboard"))
            .GetProperty("pm").GetProperty("completedThisMonth").GetInt32();

    [Fact]
    public async Task Compliance_is_a_percentage_of_what_fell_due()
    {
        var today = _factory.Services.GetRequiredService<HospitalClock>().Today();
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        var (schedule, equipment, versionId, signerId) = await SeedAsync();

        await using var db = fixture.CreateContext();
        db.PmTasks.AddRange(
            Done(schedule, equipment, monthStart, versionId, signerId, DateTime.UtcNow),
            Open(schedule, equipment, monthStart.AddDays(-1)));
        await db.SaveChangesAsync();

        var metrics = await MetricsAsync();
        var compliance = metrics.GetProperty("pm").GetProperty("compliancePercent");

        Assert.Equal(JsonValueKind.Number, compliance.ValueKind);
        Assert.InRange(compliance.GetInt32(), 0, 100);
    }

    [Fact]
    public async Task Someone_who_logs_in_this_week_counts_as_active()
    {
        var before = await MetricsAsync();
        var activeBefore = before.GetProperty("users").GetProperty("activeThisWeek").GetInt32();

        await CreateUserAsync($"pilot2-{_suffix}", Domain.Identity.Roles.BmeEngineer);
        using var second = _factory.CreateClient();
        var login = await second.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"pilot2-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();

        var after = await MetricsAsync();
        Assert.Equal(activeBefore + 1, after.GetProperty("users").GetProperty("activeThisWeek").GetInt32());
    }

    private async Task<(PmSchedule, Domain.Assets.Equipment, int, int)> SeedAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = fixture.CreateContext();

        var room = new Location
        {
            Code = $"PILOT-{tag}", Name = $"Room {tag}", Level = LocationLevel.Room,
        };
        db.Locations.Add(room);
        await db.SaveChangesAsync();

        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
        var equipment = new Domain.Assets.Equipment
        {
            AssetTag = $"PILOT-{tag}".ToUpperInvariant(),
            EquipmentTypeId = type.Id,
            LocationId = room.Id,
        };
        db.Equipment.Add(equipment);

        var template = new ChecklistTemplate
        {
            EquipmentTypeId = type.Id,
            Code = $"pilot-{tag}",
            Name = "Pilot PM",
        };
        db.ChecklistTemplates.Add(template);
        await db.SaveChangesAsync();

        // A completed task must carry the version it was filled under and who
        // signed it - the database refuses a completion missing either.
        var version = new ChecklistTemplateVersion
        {
            ChecklistTemplateId = template.Id,
            VersionNo = 1,
            Status = ChecklistVersionStatus.Published,
            PublishedAtUtc = DateTime.UtcNow,
            Definition = new ChecklistDefinition
            {
                Sections =
                [
                    new ChecklistSection
                    {
                        Title = "Safety",
                        Items = [new ChecklistItem { Key = "casing", Label = "Casing intact" }],
                    },
                ],
            },
        };
        db.ChecklistTemplateVersions.Add(version);

        var schedule = new PmSchedule
        {
            EquipmentId = equipment.Id,
            ChecklistTemplateId = template.Id,
            Frequency = PmFrequency.Monthly,
            AnchorDate = DateOnly.FromDateTime(DateTime.UtcNow),
        };
        db.PmSchedules.Add(schedule);
        await db.SaveChangesAsync();

        var signer = await db.Users.FirstAsync(u => u.UserName == $"pilot-{_suffix}");
        return (schedule, equipment, version.Id, signer.Id);
    }

    private static PmTask Open(PmSchedule schedule, Domain.Assets.Equipment equipment, DateOnly due) => new()
    {
        PmScheduleId = schedule.Id,
        EquipmentId = equipment.Id,
        DueDate = due,
        Status = PmTaskStatus.Overdue,
    };

    private static PmTask Done(
        PmSchedule schedule,
        Domain.Assets.Equipment equipment,
        DateOnly due,
        int versionId,
        int signerId,
        DateTime completedAtUtc) => new()
        {
            PmScheduleId = schedule.Id,
            EquipmentId = equipment.Id,
            DueDate = due,
            Status = PmTaskStatus.Completed,
            CompletedAtUtc = completedAtUtc,
            CompletedByUserId = signerId,
            ChecklistTemplateVersionId = versionId,
        };
}
