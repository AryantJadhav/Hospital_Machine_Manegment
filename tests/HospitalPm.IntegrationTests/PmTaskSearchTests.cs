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
/// Finding one machine's PM in the work list.
///
/// A hospital's open PM list is hundreds of rows, and the only way to reach one
/// machine's was to page through it 25 at a time - a technician holding a
/// machine with a tag on it had no way to type that tag.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PmTaskSearchTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "PmSearch2026!";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _tag = null!;
    private string _otherTag = null!;
    private string _room = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _client = _factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        _tag = $"SRCH-{suffix}".ToUpperInvariant();
        _otherTag = $"OTHR-{suffix}".ToUpperInvariant();
        _room = $"Ward {suffix}";

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = $"srch-{suffix}", FullName = "Searching Tech", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, Domain.Identity.Roles.Employee);
        }

        var login = await _client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"srch-{suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        await SeedAsync(_tag, _room, "Hamilton", "Searchable C1");
        await SeedAsync(_otherTag, $"Other {suffix}", "Philips", "V60");
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

    private async Task<string[]> SearchAsync(string q)
    {
        var page = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/pm/tasks?q={Uri.EscapeDataString(q)}&pageSize=100");
        return page.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("assetTag").GetString()!).ToArray();
    }

    [Fact]
    public async Task Typing_a_tag_finds_that_machines_task_and_no_other()
    {
        var found = await SearchAsync(_tag);

        Assert.Equal([_tag], found);
    }

    [Fact]
    public async Task Search_ignores_case_and_matches_part_of_a_tag()
    {
        var found = await SearchAsync(_tag[..8].ToLowerInvariant());

        Assert.Contains(_tag, found);
        Assert.DoesNotContain(_otherTag, found);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("maker")]
    [InlineData("place")]
    public async Task Search_also_matches_what_is_printed_on_the_machine_and_where_it_stands(string field)
    {
        var term = field switch
        {
            "model" => "searchable c1",
            "maker" => "hamilton",
            _ => _room,
        };

        var found = await SearchAsync(term);

        Assert.Contains(_tag, found);
        Assert.DoesNotContain(_otherTag, found);
    }

    [Fact]
    public async Task A_search_that_matches_nothing_returns_nothing_rather_than_everything()
    {
        Assert.Empty(await SearchAsync($"no-such-machine-{Guid.NewGuid():N}"));
    }

    [Fact]
    public async Task A_blank_search_is_no_search()
    {
        var found = await SearchAsync("   ");

        Assert.Contains(_tag, found);
        Assert.Contains(_otherTag, found);
    }

    private async Task SeedAsync(string assetTag, string roomName, string maker, string model)
    {
        await using var db = fixture.CreateContext();
        var code = Guid.NewGuid().ToString("N")[..8];

        var room = new Location { Code = $"SR-{code}", Name = roomName, Level = LocationLevel.Room };
        db.Locations.Add(room);
        await db.SaveChangesAsync();

        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
        var equipment = new Domain.Assets.Equipment
        {
            AssetTag = assetTag, EquipmentTypeId = type.Id, LocationId = room.Id,
            Manufacturer = maker, Model = model,
        };
        db.Equipment.Add(equipment);

        var template = new ChecklistTemplate
        {
            EquipmentTypeId = type.Id, Code = $"sr-{code}", Name = "Search PM",
        };
        db.ChecklistTemplates.Add(template);
        await db.SaveChangesAsync();

        var schedule = new PmSchedule
        {
            EquipmentId = equipment.Id,
            ChecklistTemplateId = template.Id,
            Frequency = PmFrequency.Monthly,
            AnchorDate = DateOnly.FromDateTime(DateTime.UtcNow),
        };
        db.PmSchedules.Add(schedule);
        await db.SaveChangesAsync();

        // Well ahead, and merely Scheduled: the work list shows it all the same,
        // and it stays out of every count of what fell due this month. The
        // dashboard's compliance test compares this month's completions with the
        // tasks that fell due, across a database every test shares, and two more
        // tasks due today tipped it.
        db.PmTasks.Add(new PmTask
        {
            PmScheduleId = schedule.Id,
            EquipmentId = equipment.Id,
            DueDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(45),
            Status = PmTaskStatus.Scheduled,
        });
        await db.SaveChangesAsync();
    }
}
