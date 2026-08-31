using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The machine record behind a QR scan, over HTTP.
///
/// This endpoint stitches six queries together, and the first version threw a
/// 500 on every request: it counted out-of-range readings with a LINQ
/// predicate over <c>PmCompletion.Answers</c>, which is a jsonb-converted
/// dictionary EF cannot translate. It compiled, and nothing below the
/// endpoint layer noticed. A real request does.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class EquipmentHistoryApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private int _equipmentId;
    private string _assetTag = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _client = _factory.CreateClient();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userName = $"hist-{suffix}";
        const string password = "History2026!";

        await using var db = fixture.CreateContext();

        // A real chain, so the breadcrumb has something to walk. "Ward 3"
        // alone does not tell a technician which building to go to.
        var building = new Location
        {
            Code = $"HB-{suffix}",
            Name = $"Block {suffix}",
            Level = LocationLevel.Building,
        };
        db.Locations.Add(building);
        await db.SaveChangesAsync();

        var room = new Location
        {
            Code = $"HR-{suffix}",
            Name = $"ICU {suffix}",
            Level = LocationLevel.Room,
            ParentId = building.Id,
        };
        db.Locations.Add(room);
        await db.SaveChangesAsync();

        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");

        _assetTag = $"HIST-{suffix}".ToUpperInvariant();

        var equipment = new Domain.Assets.Equipment
        {
            AssetTag = _assetTag,
            EquipmentTypeId = type.Id,
            LocationId = room.Id,
        };
        db.Equipment.Add(equipment);

        var template = new ChecklistTemplate
        {
            EquipmentTypeId = type.Id,
            Code = "HISTCL-" + suffix,
            Name = "Quarterly ventilator check",
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
                            new ChecklistItem
                            {
                                Key = "casing",
                                Label = "Casing",
                                Type = ChecklistItemType.PassFail,
                            },
                            new ChecklistItem
                            {
                                Key = "flow",
                                Label = "Flow",
                                Type = ChecklistItemType.Number,
                                Min = 36,
                                Max = 44,
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
            Frequency = PmFrequency.Quarterly,
            AnchorDate = new DateOnly(2026, 1, 1),
        };
        db.PmSchedules.Add(schedule);

        var engineer = new Infrastructure.Identity.ApplicationUser
        {
            UserName = "hist-eng-" + suffix,
            FullName = "R Kulkarni",
        };
        db.Users.Add(engineer);
        await db.SaveChangesAsync();

        var done = new PmTask
        {
            PmScheduleId = schedule.Id,
            EquipmentId = equipment.Id,
            DueDate = new DateOnly(2026, 1, 1),
            Status = PmTaskStatus.Due,
        };
        var open = new PmTask
        {
            PmScheduleId = schedule.Id,
            EquipmentId = equipment.Id,
            DueDate = new DateOnly(2026, 4, 1),
            Status = PmTaskStatus.Due,
        };
        db.PmTasks.AddRange(done, open);
        await db.SaveChangesAsync();

        db.PmCompletions.Add(new PmCompletion
        {
            PmTaskId = done.Id,
            ChecklistTemplateVersionId = version.Id,
            CompletedByUserId = engineer.Id,
            CompletedAtUtc = DateTime.UtcNow.AddDays(-30),
            PerformedAtUtc = DateTime.UtcNow.AddDays(-30),
            SignedByName = "R Kulkarni",
            Answers = new Dictionary<string, ChecklistAnswer>(StringComparer.Ordinal)
            {
                ["casing"] = new() { Value = "pass" },
                ["flow"] = new() { Value = "12", OutOfRange = true, Note = "Reading low, escalated" },
            },
        });

        done.Status = PmTaskStatus.Completed;
        done.CompletedAtUtc = DateTime.UtcNow.AddDays(-30);
        done.CompletedByUserId = engineer.Id;
        done.ChecklistTemplateVersionId = version.Id;

        await db.SaveChangesAsync();
        _equipmentId = equipment.Id;

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();

        var user = new Infrastructure.Identity.ApplicationUser
        {
            UserName = userName,
            FullName = "History API User",
        };
        await users.CreateAsync(user, password);
        await users.AddToRoleAsync(user, Domain.Identity.Roles.BiomedicalHead);

        var login = await _client.PostAsJsonAsync("/api/auth/login", new { userName, password });
        login.EnsureSuccessStatusCode();

        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        var created = await _client.PostAsJsonAsync("/api/work-orders", new
        {
            equipmentId = _equipmentId,
            faultDescription = "Alarm sounding with no cause",
            priority = 30,
            outOfService = true,
        });
        created.EnsureSuccessStatusCode();
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

    private async Task<JsonElement> HistoryAsync()
    {
        var res = await _client.GetAsync($"/api/equipment/{_equipmentId}/history");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task The_whole_record_comes_back_in_one_call()
    {
        var body = await HistoryAsync();

        var equipment = body.GetProperty("equipment");
        Assert.Equal(_assetTag, equipment.GetProperty("assetTag").GetString());
        Assert.False(string.IsNullOrWhiteSpace(equipment.GetProperty("equipmentTypeName").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(equipment.GetProperty("locationName").GetString()));

        Assert.Single(body.GetProperty("openPm").EnumerateArray());
        Assert.Single(body.GetProperty("completedPm").EnumerateArray());
        Assert.Single(body.GetProperty("workOrders").EnumerateArray());
    }

    [Fact]
    public async Task Out_of_range_readings_are_counted_from_the_jsonb_answers()
    {
        var body = await HistoryAsync();
        var completed = body.GetProperty("completedPm").EnumerateArray().First();

        // The regression this class exists for. One reading was recorded low;
        // the row must say so without the reader opening the certificate.
        Assert.Equal(1, completed.GetProperty("outOfRange").GetInt32());
        Assert.True(completed.GetProperty("hasCertificate").GetBoolean());
        Assert.Equal("R Kulkarni", completed.GetProperty("completedBy").GetString());
    }

    [Fact]
    public async Task The_breadcrumb_names_the_whole_chain()
    {
        var body = await HistoryAsync();

        var names = body.GetProperty("breadcrumb").EnumerateArray()
            .Select(b => b.GetProperty("name").GetString()!)
            .ToList();

        Assert.Equal(2, names.Count);
        Assert.StartsWith("Block ", names[0], StringComparison.Ordinal);
        Assert.StartsWith("ICU ", names[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_machine_reported_out_of_service_reads_as_down()
    {
        var body = await HistoryAsync();
        var summary = body.GetProperty("summary");

        Assert.True(summary.GetProperty("currentlyDown").GetBoolean());
        Assert.Equal(1, summary.GetProperty("openWorkOrderCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("completedPmCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("openPmCount").GetInt32());
    }

    [Fact]
    public async Task An_unknown_machine_is_404_not_500()
    {
        var res = await _client.GetAsync("/api/equipment/999999/history");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
