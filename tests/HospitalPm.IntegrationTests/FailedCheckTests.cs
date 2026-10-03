using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using QuestPDF.Fluent;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// A check that failed is a finding, whatever the readings say.
///
/// Only a numeric reading could be flagged. A PM where every number was in
/// range but the alarm test was answered "fail" was recorded with no finding
/// at all: the confirmation said "PM recorded.", the machine's history showed
/// nothing, and the certificate was headed "All checks within specification" -
/// the one sentence a certificate must never say when a check failed.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class FailedCheckTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "FailedCheck2026!";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _suffix = null!;

    static FailedCheckTests() =>
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _client = _factory.CreateClient();
        _suffix = Guid.NewGuid().ToString("N")[..8];

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = $"fail-{_suffix}", FullName = "Failing Tech", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, Domain.Identity.Roles.BmeEngineer);
        }

        var login = await _client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"fail-{_suffix}", password = Password });
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

    // ---- The answer itself -------------------------------------------------

    [Theory]
    [InlineData("fail", true)]
    [InlineData("FAIL", true)]
    [InlineData("pass", false)]
    [InlineData("na", false)]
    [InlineData("yes", false)]
    [InlineData("no", false)]
    [InlineData("42.5", false)]
    [InlineData("", false)]
    public void Only_a_fail_answer_is_a_failed_check(string value, bool expected)
    {
        // "no" is deliberately not a failure: on a Yes/No question it can be the
        // good answer ("Any leakage?  No").
        Assert.Equal(expected, new ChecklistAnswer { Value = value }.IsFailedCheck());
    }

    [Fact]
    public void A_completion_counts_its_failed_checks_apart_from_out_of_range_readings()
    {
        var completion = new PmCompletion
        {
            Answers = new Dictionary<string, ChecklistAnswer>
            {
                ["alarm"] = new() { Value = "fail" },
                ["casing"] = new() { Value = "pass" },
                ["flow"] = new() { Value = "9", OutOfRange = true },
            },
        };

        Assert.Equal(1, completion.FailedCheckCount);
        Assert.Equal(1, completion.OutOfRangeCount);
    }

    // ---- The certificate ---------------------------------------------------

    private static PmCertificateData Certificate(params CertificateLine[] lines) => new(
        "BME-0001", "Ventilator", "ICU", "SN-1", "Philips", "V60", "Ventilator PM", 1,
        new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 9, 1, 9, 5, 0, DateTimeKind.Utc),
        new DateOnly(2026, 9, 1), "R. Kulkarni", "R. Kulkarni", null, null, null, lines);

    private static CertificateLine Passed(string label = "Casing intact") =>
        new("Checks", label, "Pass", null, false, null);

    private static CertificateLine InRange() =>
        new("Checks", "Flow", "60", null, false, "56–64 L/min");

    private static CertificateLine Failed(string label = "Alarm triggers") =>
        new("Checks", label, "Fail", "No alarm at 15 psi", false, null, Failed: true);

    [Fact]
    public void A_failed_check_with_every_reading_in_range_is_not_clean()
    {
        var data = Certificate(Passed(), InRange(), Failed());

        Assert.False(data.IsClean);
        Assert.Equal(0, data.OutOfRangeCount);
        Assert.Equal(1, data.FailedCount);
        Assert.Equal("1 check failed", data.Verdict);
    }

    [Fact]
    public void A_certificate_with_nothing_wrong_still_says_so()
    {
        var data = Certificate(Passed(), InRange());

        Assert.True(data.IsClean);
        Assert.Equal("All checks within specification", data.Verdict);
    }

    [Fact]
    public void A_failed_check_leads_the_verdict_before_a_reading()
    {
        var data = Certificate(
            Failed("Alarm A"), Failed("Alarm B"),
            new CertificateLine("Checks", "Flow", "52", null, true, "56–64 L/min"));

        // The failure is the more serious finding, so it is said first.
        Assert.Equal("2 checks failed · 1 reading(s) outside specification", data.Verdict);
    }

    [Fact]
    public void A_certificate_with_a_failed_check_renders()
    {
        var pdf = new PmCertificateDocument(Certificate(Passed(), Failed()), new ReportOptions { HospitalName = "Test Hospital" }).GeneratePdf();

        Assert.NotEmpty(pdf);
    }

    // ---- End to end --------------------------------------------------------

    [Fact]
    public async Task Recording_a_failed_check_is_reported_in_the_response_and_the_history()
    {
        var (taskId, versionId, equipmentId) = await SeedOpenTaskAsync();

        var complete = await _client.PostAsJsonAsync($"/api/pm/tasks/{taskId}/complete", new
        {
            checklistTemplateVersionId = versionId,
            answers = new Dictionary<string, object>
            {
                // Every reading in range; only the alarm test failed.
                ["alarm"] = new { value = "fail", note = "No alarm at 15 psi" },
                ["flow"] = new { value = "60" },
            },
            signedByName = "Failing Tech",
            performedAtUtc = DateTime.UtcNow,
            clientSubmissionId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var body = await complete.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("failedCheckCount").GetInt32());
        Assert.Equal(0, body.GetProperty("outOfRangeCount").GetInt32());

        var history = await _client.GetFromJsonAsync<JsonElement>($"/api/equipment/{equipmentId}/history");
        var pm = history.GetProperty("completedPm")[0];
        Assert.Equal(1, pm.GetProperty("failedChecks").GetInt32());
        Assert.Equal(0, pm.GetProperty("outOfRange").GetInt32());

        var certificate = await _client.GetAsync($"/api/reports/pm/{taskId}/certificate.pdf");
        Assert.Equal(HttpStatusCode.OK, certificate.StatusCode);
        Assert.NotEmpty(await certificate.Content.ReadAsByteArrayAsync());
    }

    private async Task<(int TaskId, int VersionId, int EquipmentId)> SeedOpenTaskAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        await using var db = fixture.CreateContext();

        var room = new Location { Code = $"FC-{tag}", Name = $"Room {tag}", Level = LocationLevel.Room };
        db.Locations.Add(room);
        await db.SaveChangesAsync();

        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
        var equipment = new Domain.Assets.Equipment
        {
            AssetTag = $"FC-{tag}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
        };
        db.Equipment.Add(equipment);

        var template = new ChecklistTemplate
        {
            EquipmentTypeId = type.Id, Code = $"fc-{tag}", Name = "Failed-check PM",
        };
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
                Sections =
                [
                    new ChecklistSection
                    {
                        Title = "Checks",
                        Items =
                        [
                            new ChecklistItem { Key = "alarm", Label = "Alarm triggers", Type = ChecklistItemType.PassFail },
                            new ChecklistItem
                            {
                                Key = "flow", Label = "Flow", Type = ChecklistItemType.Number,
                                Unit = "L/min", Min = 56, Max = 64,
                            },
                        ],
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

        var task = new PmTask
        {
            PmScheduleId = schedule.Id,
            EquipmentId = equipment.Id,
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = PmTaskStatus.Due,
        };
        db.PmTasks.Add(task);
        await db.SaveChangesAsync();

        return (task.Id, version.Id, equipment.Id);
    }
}
