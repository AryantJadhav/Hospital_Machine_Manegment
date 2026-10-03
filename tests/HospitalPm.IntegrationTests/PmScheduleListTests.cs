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
/// The schedule list is paged.
///
/// It used to return every schedule in one response. At the size the build plan
/// sets for a large hospital (15,000 machines) that was a 3.6 MB answer to a
/// single request; paged, the same request is about 12 KB.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PmScheduleListTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "SchedList2026!";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _suffix = null!;

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
                UserName = $"sl-{_suffix}", FullName = "Schedule Lister", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, Domain.Identity.Roles.BmeEngineer);
        }

        var login = await _client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"sl-{_suffix}", password = Password });
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

    [Fact]
    public async Task One_page_is_returned_with_the_total_and_never_more_than_asked_for()
    {
        var equipmentId = await SeedMachineWithSchedulesAsync(3);

        var page = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/pm/schedules?equipmentId={equipmentId}&pageSize=2");

        Assert.Equal(3, page.GetProperty("total").GetInt32());
        Assert.Equal(2, page.GetProperty("items").GetArrayLength());
        Assert.Equal(2, page.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Pages_do_not_overlap_and_together_hold_every_schedule()
    {
        var equipmentId = await SeedMachineWithSchedulesAsync(3);

        var ids = new List<int>();
        for (var p = 1; p <= 2; p++)
        {
            var page = await _client.GetFromJsonAsync<JsonElement>(
                $"/api/pm/schedules?equipmentId={equipmentId}&pageSize=2&page={p}");
            ids.AddRange(page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()));
        }

        Assert.Equal(3, ids.Count);
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public async Task An_unreasonable_page_size_is_capped()
    {
        var page = await _client.GetFromJsonAsync<JsonElement>("/api/pm/schedules?pageSize=1000000");

        Assert.True(page.GetProperty("pageSize").GetInt32() <= 200);
    }

    private async Task<int> SeedMachineWithSchedulesAsync(int schedules)
    {
        await using var db = fixture.CreateContext();

        var room = new Location { Code = $"SL-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
        db.Locations.Add(room);
        await db.SaveChangesAsync();

        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
        var equipment = new Domain.Assets.Equipment
        {
            AssetTag = $"SL-{_suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
        };
        db.Equipment.Add(equipment);
        await db.SaveChangesAsync();

        for (var k = 0; k < schedules; k++)
        {
            var template = new ChecklistTemplate
            {
                EquipmentTypeId = type.Id, Code = $"sl-{_suffix}-{k}", Name = $"List PM {k}",
            };
            db.ChecklistTemplates.Add(template);
            await db.SaveChangesAsync();

            db.PmSchedules.Add(new PmSchedule
            {
                EquipmentId = equipment.Id,
                ChecklistTemplateId = template.Id,
                Frequency = PmFrequency.Monthly,
                AnchorDate = DateOnly.FromDateTime(DateTime.UtcNow),
            });
        }

        await db.SaveChangesAsync();
        return equipment.Id;
    }
}
