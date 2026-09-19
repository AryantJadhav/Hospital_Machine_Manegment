using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Domain.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The PM compliance figure on the dashboard.
///
/// It is described in the code as the headline number an auditor asks for, and
/// it was wrong in a way that only showed up on a database with real history:
/// the numerator counted every PM completed this month — including a year of
/// backlog being cleared — while the denominator counted only the PMs that
/// fell due this month. Two different populations.
///
/// On a demo hospital with 418 completions and nothing yet due this month it
/// displayed nothing at all. On a department a few days further into the month
/// it would have displayed a number well over 100%.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DashboardApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _suffix = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _client = _factory.CreateClient();
        _suffix = Guid.NewGuid().ToString("N")[..8];

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();

        var userName = $"dash-{_suffix}";
        const string password = "Dashboard2026!";
        var user = new Infrastructure.Identity.ApplicationUser
        {
            UserName = userName, FullName = "Dashboard User",
        };
        await users.CreateAsync(user, password);
        await users.AddToRoleAsync(user, Domain.Identity.Roles.Admin);

        var login = await _client.PostAsJsonAsync("/api/auth/login", new { userName, password });
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

    /// <summary>
    /// Clearing a backlog must not inflate this month's compliance.
    ///
    /// Three PMs on one machine: one due this month and done, one due this
    /// month and not done, and one that fell due last year and was cleared
    /// today. Compliance for the month is one of two — 50% — and the backlog
    /// PM belongs in "completed this month" but nowhere near the ratio.
    ///
    /// The old calculation gave 150%.
    /// </summary>
    [Fact]
    public async Task Compliance_counts_only_the_work_that_fell_due_this_month()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        await using var db = fixture.CreateContext();

        var room = new Location
        {
            Code = $"DASH-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room,
        };
        db.Locations.Add(room);
        await db.SaveChangesAsync();

        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
        var equipment = new Domain.Assets.Equipment
        {
            AssetTag = $"DASH-{_suffix}".ToUpperInvariant(),
            EquipmentTypeId = type.Id,
            LocationId = room.Id,
        };
        db.Equipment.Add(equipment);

        var template = new ChecklistTemplate
        {
            EquipmentTypeId = type.Id,
            Code = $"dash-{_suffix}",
            Name = "Dashboard PM",
        };
        db.ChecklistTemplates.Add(template);
        await db.SaveChangesAsync();

        // A completed task must carry the version it was filled under and who
        // signed it — the database refuses anything less, because a completion
        // missing either is not evidence.
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
            AnchorDate = monthStart,
        };
        db.PmSchedules.Add(schedule);
        await db.SaveChangesAsync();

        var signer = await db.Users.FirstAsync(u => u.UserName == $"dash-{_suffix}");

        // Two due this month, one of them done; one long overdue, cleared today.
        db.PmTasks.AddRange(
            Done(schedule, equipment, monthStart, version.Id, signer.Id),
            Open(schedule, equipment, monthStart.AddDays(1)),
            Done(schedule, equipment, monthStart.AddYears(-1), version.Id, signer.Id));
        await db.SaveChangesAsync();

        var dashboard = await _client.GetFromJsonAsync<JsonElement>("/api/dashboard");
        var pm = dashboard.GetProperty("pm");

        var compliance = pm.GetProperty("complianceThisMonth");
        var completedThisMonth = pm.GetProperty("completedThisMonth").GetInt32();

        // Asserted as a property rather than an exact percentage, because the
        // dashboard counts the whole database and the suite shares one. An
        // exact figure here passes alone and fails in a full run — which is a
        // test measuring the wrong thing, not a product that behaves
        // differently. This is the invariant the bug broke.
        Assert.NotEqual(JsonValueKind.Null, compliance.ValueKind);
        Assert.InRange(compliance.GetInt32(), 0, 100);

        // And the shape that produced the impossible figure is present: far
        // more completions recorded this month than PMs that fell due in it,
        // because a backlog is being cleared. The old calculation divided the
        // first by the second.
        var dueSoFar = await db.PmTasks.CountAsync(
            t => t.DueDate >= monthStart && t.DueDate <= today);
        Assert.True(
            completedThisMonth > dueSoFar,
            $"expected the backlog case ({completedThisMonth} completed vs {dueSoFar} due), "
            + "which is what made the old ratio exceed 100%");
    }

    /// <summary>
    /// The dashboard reports the newest backup attempt, not just the newest
    /// good one: a failure tonight with a good backup from yesterday is a
    /// warning, and a success after it clears it.
    ///
    /// The "no recent good backup" state is not asserted here. The suite shares
    /// one database and other tests leave recent successes in it, so that
    /// state cannot be reached without deleting their rows.
    /// </summary>
    [Fact]
    public async Task Backup_status_follows_the_newest_attempt()
    {
        await using var db = fixture.CreateContext();
        var now = DateTime.UtcNow;

        db.BackupRuns.Add(BackupRunAt(now.AddMinutes(-2), BackupStatus.Succeeded));
        db.BackupRuns.Add(BackupRunAt(now.AddMinutes(-1), BackupStatus.Failed));
        await db.SaveChangesAsync();

        var failed = (await _client.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("backup");
        Assert.Equal("warn", failed.GetProperty("state").GetString());
        Assert.NotEqual(JsonValueKind.Null, failed.GetProperty("lastSuccessUtc").ValueKind);

        db.BackupRuns.Add(BackupRunAt(DateTime.UtcNow, BackupStatus.Succeeded));
        await db.SaveChangesAsync();

        var recovered = (await _client.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("backup");
        Assert.Equal("ok", recovered.GetProperty("state").GetString());
    }

    /// <summary>
    /// An employee cannot open the Backups page, so the dashboard does not
    /// tell them about it either.
    /// </summary>
    [Fact]
    public async Task Backup_status_is_for_administrators_only()
    {
        var userName = $"dash-emp-{_suffix}";
        const string password = "Dashboard2026!";

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var employee = new Infrastructure.Identity.ApplicationUser
            {
                UserName = userName, FullName = "Dashboard Employee", IsActive = true,
            };
            Assert.True((await users.CreateAsync(employee, password)).Succeeded);
            await users.AddToRoleAsync(employee, Domain.Identity.Roles.Employee);
        }

        using var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        var dashboard = await client.GetFromJsonAsync<JsonElement>("/api/dashboard");
        Assert.Equal(JsonValueKind.Null, dashboard.GetProperty("backup").ValueKind);
    }

    private static BackupRun BackupRunAt(DateTime startedAtUtc, BackupStatus status) => new()
    {
        StartedAtUtc = startedAtUtc,
        FinishedAtUtc = startedAtUtc.AddSeconds(5),
        Status = status,
        Trigger = BackupTrigger.Scheduled,
        Error = status == BackupStatus.Failed ? "test failure" : null,
    };

    private static PmTask Open(
        PmSchedule schedule, Domain.Assets.Equipment equipment, DateOnly due) => new()
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
        int signerId) => new()
        {
            PmScheduleId = schedule.Id,
            EquipmentId = equipment.Id,
            DueDate = due,
            Status = PmTaskStatus.Completed,
            CompletedAtUtc = DateTime.UtcNow,
            CompletedByUserId = signerId,
            ChecklistTemplateVersionId = versionId,
        };
}
