using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Scheduling one checklist across a whole equipment type.
///
/// This endpoint exists because creating schedules one at a time meant a
/// hospital with 2,000 assets faced 2,000 requests to set up preventive
/// maintenance. That is not a slow path; it is a path nobody walks, and it is
/// why the product could hold an equipment register and never schedule a PM.
///
/// Over HTTP, because the interesting behaviour is what it refuses and what
/// it silently skips, and both are shaped by the response a caller reads.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class BulkScheduleApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private int _templateId;
    private int _wardId;
    private string _suffix = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _client = _factory.CreateClient();
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using var db = fixture.CreateContext();

        // Two wards, so the location filter has something to exclude.
        var ward = new Location
        {
            Code = $"BSW-{_suffix}", Name = $"Ward {_suffix}", Level = LocationLevel.Department,
        };
        var otherWard = new Location
        {
            Code = $"BSO-{_suffix}", Name = $"Other {_suffix}", Level = LocationLevel.Department,
        };
        db.Locations.AddRange(ward, otherWard);
        await db.SaveChangesAsync();
        _wardId = ward.Id;

        // An equipment type of this test's own, so counts are not disturbed by
        // whatever else the suite has left in the database.
        var type = new EquipmentType
        {
            Code = $"bulk-type-{_suffix}",
            Name = $"Bulk test device {_suffix}",
            IsActive = true,
        };
        db.EquipmentTypes.Add(type);
        await db.SaveChangesAsync();

        // Four in the ward, one elsewhere, covering the statuses that decide
        // whether a machine is maintained.
        db.Equipment.AddRange(
            Machine($"A", type.Id, ward.Id, EquipmentStatus.InService),
            Machine($"B", type.Id, ward.Id, EquipmentStatus.UnderRepair),
            Machine($"C", type.Id, ward.Id, EquipmentStatus.InStore),
            Machine($"D", type.Id, ward.Id, EquipmentStatus.Condemned),
            Machine($"E", type.Id, otherWard.Id, EquipmentStatus.InService));
        await db.SaveChangesAsync();

        var template = new ChecklistTemplate
        {
            EquipmentTypeId = type.Id,
            Code = $"bulk-cl-{_suffix}",
            Name = "Bulk quarterly PM",
        };
        db.ChecklistTemplates.Add(template);
        await db.SaveChangesAsync();
        _templateId = template.Id;

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();

        var userName = $"bulk-{_suffix}";
        const string password = "BulkSchedule2026!";
        var user = new Infrastructure.Identity.ApplicationUser
        {
            UserName = userName,
            FullName = "Bulk Schedule User",
        };
        await users.CreateAsync(user, password);
        await users.AddToRoleAsync(user, Domain.Identity.Roles.BmeHead);

        var login = await _client.PostAsJsonAsync("/api/auth/login", new { userName, password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
    }

    private Domain.Assets.Equipment Machine(
        string tag, int typeId, int locationId, EquipmentStatus status) => new()
        {
            AssetTag = $"BULK-{_suffix}-{tag}".ToUpperInvariant(),
            EquipmentTypeId = typeId,
            LocationId = locationId,
            Status = status,
        };

    private async Task PublishAsync()
    {
        await using var db = fixture.CreateContext();
        db.ChecklistTemplateVersions.Add(new ChecklistTemplateVersion
        {
            ChecklistTemplateId = _templateId,
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
        });
        await db.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> ScheduleAsync(int? locationId = null, bool includeInStore = false)
        => _client.PostAsJsonAsync("/api/pm/schedules/bulk", new
        {
            checklistTemplateId = _templateId,
            frequency = 20,               // Quarterly
            intervalDays = 0,
            anchorDate = "2026-01-01",
            graceDays = 7,
            locationId,
            includeInStore,
        });

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
    /// A draft can still be edited, and a completion has to be tied to the
    /// exact version it was filled under. Scheduling against a checklist that
    /// has never been published would promise a technician work with no
    /// questions in it.
    /// </summary>
    [Fact]
    public async Task An_unpublished_checklist_cannot_be_scheduled()
    {
        var response = await ScheduleAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("never been published", body, StringComparison.OrdinalIgnoreCase);

        await using var db = fixture.CreateContext();
        Assert.Equal(0, await db.PmSchedules.CountAsync(s => s.ChecklistTemplateId == _templateId));
    }

    [Fact]
    public async Task Scheduling_covers_the_fleet_and_says_what_it_skipped()
    {
        await PublishAsync();

        var response = await ScheduleAsync();
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // In service and under repair are maintained. In store is not yet in
        // use, and condemned is never maintained again.
        Assert.Equal(5, body.GetProperty("considered").GetInt32());
        Assert.Equal(3, body.GetProperty("created").GetInt32());
        Assert.Equal(1, body.GetProperty("skippedRetired").GetInt32());
        Assert.Equal(1, body.GetProperty("skippedNotYetInService").GetInt32());

        await using var db = fixture.CreateContext();
        Assert.Equal(3, await db.PmSchedules.CountAsync(s => s.ChecklistTemplateId == _templateId));

        // Generated immediately, so the work appears rather than waiting for
        // the nightly job.
        Assert.True(
            await db.PmTasks.CountAsync(t => t.Schedule!.ChecklistTemplateId == _templateId) > 0,
            "bulk scheduling should produce PM tasks without waiting for the nightly run");
    }

    /// <summary>
    /// Running it again after commissioning another ward must add only what is
    /// new. "Some of these are already done" is the normal case, not an error.
    /// </summary>
    [Fact]
    public async Task Running_it_again_adds_nothing_and_does_not_fail()
    {
        await PublishAsync();

        (await ScheduleAsync()).EnsureSuccessStatusCode();

        var second = await ScheduleAsync();
        second.EnsureSuccessStatusCode();
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0, body.GetProperty("created").GetInt32());
        Assert.Equal(3, body.GetProperty("alreadyScheduled").GetInt32());

        await using var db = fixture.CreateContext();
        Assert.Equal(3, await db.PmSchedules.CountAsync(s => s.ChecklistTemplateId == _templateId));
    }

    /// <summary>
    /// A large hospital commissions a ward at a time, not a fleet at once.
    /// </summary>
    [Fact]
    public async Task A_location_narrows_it_to_one_part_of_the_hospital()
    {
        await PublishAsync();

        var response = await ScheduleAsync(locationId: _wardId);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Four machines are in the ward; the fifth is elsewhere and untouched.
        Assert.Equal(4, body.GetProperty("considered").GetInt32());
        Assert.Equal(2, body.GetProperty("created").GetInt32());
    }

    [Fact]
    public async Task In_store_machines_are_included_only_when_asked_for()
    {
        await PublishAsync();

        var response = await ScheduleAsync(includeInStore: true);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(4, body.GetProperty("created").GetInt32());
        Assert.Equal(0, body.GetProperty("skippedNotYetInService").GetInt32());

        // Still never the condemned one.
        Assert.Equal(1, body.GetProperty("skippedRetired").GetInt32());
    }

    [Fact]
    public async Task An_unknown_location_is_refused_rather_than_silently_ignored()
    {
        await PublishAsync();

        var response = await ScheduleAsync(locationId: 999_999);

        // Falling back to "everything" would schedule a whole hospital when
        // someone meant one ward.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await using var db = fixture.CreateContext();
        Assert.Equal(0, await db.PmSchedules.CountAsync(s => s.ChecklistTemplateId == _templateId));
    }

    [Fact]
    public async Task An_employee_cannot_schedule_the_hospital()
    {
        await PublishAsync();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userName = $"bulk-tech-{suffix}";
        const string password = "BulkTech2026!";

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
        var user = new Infrastructure.Identity.ApplicationUser
        {
            UserName = userName, FullName = "Bulk Technician",
        };
        await users.CreateAsync(user, password);
        await users.AddToRoleAsync(user, Domain.Identity.Roles.BmeEngineer);

        using var technician = _factory.CreateClient();
        var login = await technician.PostAsJsonAsync("/api/auth/login", new { userName, password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        technician.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        var response = await technician.PostAsJsonAsync("/api/pm/schedules/bulk", new
        {
            checklistTemplateId = _templateId,
            frequency = 20,
            intervalDays = 0,
            anchorDate = "2026-01-01",
            graceDays = 7,
            locationId = (int?)null,
            includeInStore = false,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
