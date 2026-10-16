using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Reports;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>The training report's counting, with no database.</summary>
public sealed class TrainingSummaryReportCountingTests
{
    private static readonly DateOnly Today = new(2026, 10, 12);

    private static TrainingSessionInput Session(
        int id, string title, DateOnly date, string? type, int? minutes, params TrainingAttendeeInput[] who) =>
        new(id, title, date, type, null, null, null, null, null, minutes, who);

    private static TrainingAttendeeInput Person(string name, int? userId = null, string? designation = null) =>
        new(userId, name, designation);

    [Fact]
    public void A_planned_session_is_listed_but_counts_for_nothing()
    {
        var report = TrainingSummaryReport.Build(
        [
            Session(1, "Held", Today.AddDays(-3), "Ventilator", 60, Person("Asha"), Person("Ravi")),
            Session(2, "Planned", Today.AddDays(5), "Ventilator", 120, Person("Asha")),
        ], Today.AddDays(-30), Today.AddDays(30), Today);

        Assert.Equal(2, report.Sessions.Count);
        Assert.Equal(1, report.Held);
        Assert.Equal(1, report.Planned);
        Assert.Equal(2, report.Attendances);
        Assert.Equal(1.0, report.Hours);
        Assert.Equal(2.0, report.PersonHours);
        // Latest first, so the planned one is on top.
        Assert.Equal("Planned", report.Sessions[0].Title);
    }

    [Fact]
    public void A_person_at_three_sessions_is_one_person_and_three_places()
    {
        var report = TrainingSummaryReport.Build(
        [
            Session(1, "A", Today.AddDays(-9), "Ventilator", 60, Person("Asha Patil", 7, "Staff nurse")),
            Session(2, "B", Today.AddDays(-6), "Infusion pump", 30, Person("asha  PATIL", 7), Person("Ravi")),
            Session(3, "C", Today.AddDays(-2), "Ventilator", 90, Person("Asha Patil", 7), Person("RAVI")),
        ], Today.AddDays(-30), Today, Today);

        Assert.Equal(5, report.Attendances);
        Assert.Equal(2, report.PeopleTrained);

        var asha = Assert.Single(report.People, p => p.UserId == 7);
        Assert.Equal(3, asha.Sessions);
        Assert.Equal(3.0, asha.Hours); // 60 + 30 + 90 minutes
        Assert.Equal("Staff nurse", asha.Designation);

        // Ravi has no account: told apart by name, whatever the case.
        var ravi = Assert.Single(report.People, p => p.UserId is null);
        Assert.Equal(2, ravi.Sessions);
        Assert.Equal(2.0, ravi.Hours);
    }

    [Fact]
    public void A_name_entered_twice_on_one_session_is_one_person_present_once()
    {
        var report = TrainingSummaryReport.Build(
            [Session(1, "A", Today, "Ventilator", 60, Person("Meera"), Person("Meera"))], Today, Today, Today);

        var meera = Assert.Single(report.People);
        Assert.Equal(1, meera.Sessions);
        Assert.Equal(1.0, meera.Hours);
    }

    [Fact]
    public void Sessions_are_grouped_by_the_kind_of_machine_and_those_about_none_come_last()
    {
        var report = TrainingSummaryReport.Build(
        [
            Session(1, "Safety", Today.AddDays(-1), null, 45, Person("A")),
            Session(2, "V1", Today.AddDays(-2), "Ventilator", 60, Person("A"), Person("B")),
            Session(3, "V2", Today.AddDays(-3), "Ventilator", 60, Person("A")),
            Session(4, "P1", Today.AddDays(-4), "Infusion pump", 60, Person("A"), Person("B"), Person("C")),
            Session(5, "G2", Today.AddDays(-5), "", 30, Person("A")),
        ], Today.AddDays(-30), Today, Today);

        Assert.Equal(["Ventilator", "Infusion pump", TrainingSummaryReport.General], report.ByType.Select(t => t.EquipmentType));
        Assert.Equal(2, report.ByType[0].Sessions);
        Assert.Equal(3, report.ByType[0].Attendances);
        Assert.Equal(2, report.ByType[2].Sessions); // the two with no kind of machine, null and empty together
    }

    [Fact]
    public void A_session_with_no_length_is_counted_and_said_so_not_taken_for_zero_hours_quietly()
    {
        var report = TrainingSummaryReport.Build(
        [
            Session(1, "A", Today, "Ventilator", 120, Person("A")),
            Session(2, "B", Today, "Ventilator", null, Person("A"), Person("B")),
        ], Today, Today, Today);

        Assert.Equal(2, report.Held);
        Assert.Equal(1, report.WithoutDuration);
        Assert.Equal(2.0, report.Hours);
    }

    [Fact]
    public void An_empty_period_has_empty_everything()
    {
        var report = TrainingSummaryReport.Build([], Today, Today, Today);
        Assert.Empty(report.Sessions);
        Assert.Equal(0, report.Held);
        Assert.Equal(0, report.PeopleTrained);
        Assert.Equal(0.0, report.Hours);
    }

    [Fact]
    public void The_csv_has_a_row_for_every_session_and_quotes_what_needs_it()
    {
        var report = TrainingSummaryReport.Build(
            [Session(1, "Alarms, limits and \"silence\"", Today, "Ventilator", 60, Person("Asha"), Person("Ravi"))], Today, Today, Today);

        var lines = TrainingSummaryReport.ToCsv(report).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"Alarms, limits and \"\"silence\"\"\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("Asha; Ravi", lines[1], StringComparison.Ordinal);
        Assert.Contains(",Held,", lines[1], StringComparison.Ordinal);
    }
}

/// <summary>The training report over HTTP, against the real database.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class TrainingSummaryReportApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "TrainReport2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _engineer = null!;
    private string _suffix = null!;
    private int _typeId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var type = new EquipmentType { Code = $"tsr-{_suffix}", Name = $"Report type {_suffix}" };
            db.EquipmentTypes.Add(type);
            await db.SaveChangesAsync();
            _typeId = type.Id;
        }

        _admin = await SignInAsync("tsr-head", Roles.BmeHead);
        _engineer = await SignInAsync("tsr-eng", Roles.BmeEngineer);
    }

    private async Task<HttpClient> SignInAsync(string prefix, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = $"{prefix}-{_suffix}", FullName = $"{prefix} person", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, role);
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = $"{prefix}-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _admin?.Dispose();
        _engineer?.Dispose();
        _factory?.Dispose();
    }

    // The hospital's day, as the report counts it: a fixed offset, not the machine's clock zone.
    private static string Day(int daysFromToday) =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(330).AddDays(daysFromToday)).ToString("yyyy-MM-dd");

    private async Task AddAsync(string title, int daysFromToday, int? minutes, params object[] attendees)
    {
        var res = await _admin.PostAsJsonAsync("/api/training", new
        {
            title = $"{title} {_suffix}",
            sessionDate = Day(daysFromToday),
            equipmentTypeId = _typeId,
            trainerType = "Vendor",
            trainer = "Dr Rao",
            durationMinutes = minutes,
            attendees,
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
    }

    [Fact]
    public async Task The_report_counts_the_sessions_in_the_period_and_lists_who_came()
    {
        // Far from any other test's dates, so only these are in this window.
        await AddAsync("Alarms", -400, 60, new { name = $"Asha {_suffix}", designation = "Staff nurse" }, new { name = $"Ravi {_suffix}" });
        await AddAsync("Pumps", -398, 30, new { name = $"Asha {_suffix}" });
        await AddAsync("Outside the window", -300, 60, new { name = $"Outsider {_suffix}" });

        var report = await _admin.GetFromJsonAsync<JsonElement>($"/api/reports/training?from={Day(-401)}&to={Day(-397)}");

        Assert.Equal(2, report.GetProperty("held").GetInt32());
        Assert.Equal(0, report.GetProperty("planned").GetInt32());
        Assert.Equal(3, report.GetProperty("attendances").GetInt32());
        Assert.Equal(2, report.GetProperty("peopleTrained").GetInt32());
        Assert.Equal(1.5, report.GetProperty("hours").GetDouble());
        Assert.Equal(2.5, report.GetProperty("personHours").GetDouble()); // 1 hour for 2 people + half an hour for 1
    }

    [Fact]
    public async Task A_planned_session_in_the_period_is_listed_and_counts_for_nothing_yet()
    {
        await AddAsync("Planned", 200, 60, new { name = $"Future {_suffix}" });

        var report = await _admin.GetFromJsonAsync<JsonElement>($"/api/reports/training?from={Day(199)}&to={Day(201)}");

        Assert.Equal(0, report.GetProperty("held").GetInt32());
        Assert.Equal(1, report.GetProperty("planned").GetInt32());
        Assert.Equal(0, report.GetProperty("attendances").GetInt32());
        Assert.Equal(1, report.GetProperty("items").GetArrayLength());
        Assert.True(report.GetProperty("items")[0].GetProperty("planned").GetBoolean());
        Assert.Equal("Vendor / Manufacturer", report.GetProperty("items")[0].GetProperty("trainerType").GetString());
    }

    [Fact]
    public async Task The_csv_downloads_and_an_engineer_is_refused_both()
    {
        await AddAsync("For the csv", -500, 45, new { name = $"Csv Person {_suffix}" });

        var csv = await _admin.GetAsync($"/api/reports/training/report.csv?from={Day(-501)}&to={Day(-499)}");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.StartsWith("text/csv", csv.Content.Headers.ContentType!.MediaType, StringComparison.Ordinal);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains($"For the csv {_suffix}", text, StringComparison.Ordinal);
        Assert.Contains($"Csv Person {_suffix}", text, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.Forbidden, (await _engineer.GetAsync("/api/reports/training")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _engineer.GetAsync("/api/reports/training/report.csv")).StatusCode);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/reports/training")).StatusCode);
    }

    [Fact]
    public async Task A_period_that_ends_before_it_starts_is_refused_and_nothing_asked_gives_the_financial_year()
    {
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _admin.GetAsync($"/api/reports/training?from={Day(0)}&to={Day(-5)}")).StatusCode);

        var report = await _admin.GetFromJsonAsync<JsonElement>("/api/reports/training");
        var from = DateOnly.Parse(report.GetProperty("from").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(4, from.Month);
        Assert.Equal(1, from.Day);
    }
}
