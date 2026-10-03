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
/// Setting up a machine's PM in the same request that adds it.
///
/// The administrator says which checklist, how often (monthly, every 2 months,
/// quarterly, half-yearly or yearly) and the date the first one falls due, and the
/// rest of the dates follow from that. It is one request so a machine is never left
/// added without the PM that was asked for, or scheduled without a machine.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class EquipmentPmSetupTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "EquipPm2026!";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private string _suffix = null!;
    private int _typeId;
    private int _roomId;
    private int _templateId;
    private int _otherTypeTemplateId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _client = _factory.CreateClient();
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var room = new Location { Code = $"PS-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();
            _roomId = room.Id;

            var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
            var otherType = await db.EquipmentTypes.FirstAsync(t => t.Code == "ultrasound-scanner");
            _typeId = type.Id;

            var mine = new ChecklistTemplate { EquipmentTypeId = type.Id, Code = $"ps-{_suffix}", Name = "Setup PM" };
            var other = new ChecklistTemplate { EquipmentTypeId = otherType.Id, Code = $"ps-o-{_suffix}", Name = "Other PM" };
            db.ChecklistTemplates.AddRange(mine, other);
            await db.SaveChangesAsync();
            _templateId = mine.Id;
            _otherTypeTemplateId = other.Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = $"ps-{_suffix}", FullName = "PM Setup Admin", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, Domain.Identity.Roles.BmeHead);
        }

        var login = await _client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"ps-{_suffix}", password = Password });
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

    private string Tag(string kind) => $"PS-{kind}-{_suffix}";

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

    private object Body(string tag, object? pm) => new
    {
        assetTag = tag,
        equipmentTypeId = _typeId,
        locationId = _roomId,
        pm,
    };

    private object Pm(PmFrequency frequency, DateOnly first, int? template = null, int grace = 7) => new
    {
        checklistTemplateId = template ?? _templateId,
        frequency = (int)frequency,
        firstDueDate = first.ToString("yyyy-MM-dd"),
        graceDays = grace,
    };

    private async Task<bool> ExistsAsync(string tag)
    {
        await using var db = fixture.CreateContext();
        return await db.Equipment.AnyAsync(e => e.AssetTag == tag);
    }

    [Theory]
    [InlineData(PmFrequency.Monthly)]
    [InlineData(PmFrequency.EveryTwoMonths)]
    [InlineData(PmFrequency.Quarterly)]
    [InlineData(PmFrequency.HalfYearly)]
    [InlineData(PmFrequency.Yearly)]
    public async Task A_machine_added_with_a_PM_is_scheduled_at_that_frequency(PmFrequency frequency)
    {
        var first = Today().AddDays(20);
        var tag = Tag($"F{(int)frequency}");

        var created = await _client.PostAsJsonAsync("/api/equipment", Body(tag, Pm(frequency, first, grace: 3)));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        await using var db = fixture.CreateContext();
        var schedule = await db.PmSchedules.SingleAsync(s => s.EquipmentId == id);
        Assert.Equal(frequency, schedule.Frequency);
        Assert.Equal(0, schedule.IntervalDays);
        Assert.Equal(first, schedule.AnchorDate);
        Assert.Equal(3, schedule.GraceDays);
        Assert.Equal(_templateId, schedule.ChecklistTemplateId);
        Assert.True(schedule.IsActive);
    }

    [Fact]
    public async Task The_first_due_date_is_on_the_work_list_straight_away()
    {
        // A year out is inside no window for anything but the first date, whatever the
        // frequency: exactly one task, on the day chosen.
        var first = Today().AddDays(10);
        var created = await _client.PostAsJsonAsync(
            "/api/equipment", Body(Tag("T"), Pm(PmFrequency.Yearly, first)));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        await using var db = fixture.CreateContext();
        var task = await db.PmTasks.SingleAsync(t => t.EquipmentId == id);

        Assert.Equal(first, task.DueDate);
        Assert.Equal(PmTaskStatus.Scheduled, task.Status);
    }

    [Fact]
    public async Task A_machine_added_without_a_PM_has_no_schedule()
    {
        var created = await _client.PostAsJsonAsync("/api/equipment", Body(Tag("N"), pm: null));
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        await using var db = fixture.CreateContext();
        Assert.False(await db.PmSchedules.AnyAsync(s => s.EquipmentId == id));
    }

    [Fact]
    public async Task A_checklist_for_a_different_type_is_refused_and_the_machine_is_not_added()
    {
        var refused = await _client.PostAsJsonAsync(
            "/api/equipment", Body(Tag("W"), Pm(PmFrequency.Quarterly, Today(), template: _otherTypeTemplateId)));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("different equipment type", (await refused.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString());
        Assert.False(await ExistsAsync(Tag("W")));
    }

    [Fact]
    public async Task An_unknown_checklist_is_refused_and_the_machine_is_not_added()
    {
        var refused = await _client.PostAsJsonAsync(
            "/api/equipment", Body(Tag("U"), Pm(PmFrequency.Quarterly, Today(), template: 2_000_000_000)));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False(await ExistsAsync(Tag("U")));
    }

    [Theory]
    [InlineData(PmFrequency.Custom)]
    [InlineData((PmFrequency)99)]
    [InlineData((PmFrequency)0)]
    public async Task Only_the_named_frequencies_are_accepted(PmFrequency frequency)
    {
        var tag = Tag($"X{(int)frequency}");

        var refused = await _client.PostAsJsonAsync("/api/equipment", Body(tag, Pm(frequency, Today())));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False(await ExistsAsync(tag));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(91)]
    public async Task Grace_days_have_to_make_sense(int grace)
    {
        var tag = Tag($"G{grace + 1}");

        var refused = await _client.PostAsJsonAsync(
            "/api/equipment", Body(tag, Pm(PmFrequency.Monthly, Today(), grace: grace)));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False(await ExistsAsync(tag));
    }

    [Fact]
    public async Task A_first_due_date_that_is_not_a_date_is_refused()
    {
        var tag = Tag("D");
        var refused = await _client.PostAsJsonAsync(
            "/api/equipment", Body(tag, Pm(PmFrequency.Monthly, new DateOnly(1999, 1, 1))));

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False(await ExistsAsync(tag));
    }

    private async Task<List<string>> PreviewAsync(PmFrequency frequency, string anchor)
    {
        var body = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/pm/preview?frequency={(int)frequency}&anchorDate={anchor}");
        return body.GetProperty("dates").EnumerateArray().Select(d => d.GetString()!).ToList();
    }

    [Theory]
    [InlineData(PmFrequency.Monthly, 12)]
    [InlineData(PmFrequency.EveryTwoMonths, 6)]
    [InlineData(PmFrequency.Quarterly, 4)]
    [InlineData(PmFrequency.HalfYearly, 2)]
    [InlineData(PmFrequency.Yearly, 1)]
    public async Task The_preview_gives_the_dates_in_the_first_year(PmFrequency frequency, int timesAYear)
    {
        var body = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/pm/preview?frequency={(int)frequency}&anchorDate=2026-10-15");

        var dates = body.GetProperty("dates").EnumerateArray().Select(d => d.GetString()!).ToList();

        Assert.Equal(timesAYear, body.GetProperty("timesPerYear").GetInt32());
        Assert.Equal(timesAYear, dates.Count);
        Assert.Equal("2026-10-15", dates[0]);
        // Never the anniversary itself: that is the start of the next year.
        Assert.DoesNotContain("2027-10-15", dates);
    }

    [Fact]
    public async Task The_preview_steps_by_calendar_months_and_does_not_drift_from_the_month_end()
    {
        Assert.Equal(
            new[] { "2026-01-31", "2026-03-31", "2026-05-31", "2026-07-31", "2026-09-30", "2026-11-30" },
            await PreviewAsync(PmFrequency.EveryTwoMonths, "2026-01-31"));

        Assert.Equal(
            new[] { "2026-01-31", "2026-02-28", "2026-03-31", "2026-04-30" },
            (await PreviewAsync(PmFrequency.Monthly, "2026-01-31")).Take(4).ToArray());
    }

    [Theory]
    [InlineData(PmFrequency.Custom)]
    [InlineData((PmFrequency)99)]
    public async Task The_preview_refuses_a_frequency_it_cannot_work_out(PmFrequency frequency)
    {
        var res = await _client.GetAsync($"/api/pm/preview?frequency={(int)frequency}&anchorDate=2026-10-15");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public void Every_two_months_is_two_calendar_months()
    {
        Assert.Equal(2, PmFrequency.EveryTwoMonths.Months());
        Assert.Equal(
            new DateOnly(2026, 7, 31),
            PmDueDates.Occurrence(new DateOnly(2026, 1, 31), PmFrequency.EveryTwoMonths, 0, 3));
    }
}
