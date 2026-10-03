using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The bell in the top bar: PMs that are overdue, and PMs that fall due in the next seven
/// days, the same for everyone who is signed in.
///
/// The list is worked out from the tasks when it is asked for. Nothing is sent, so
/// there is nothing to go missing, and it works with no internet and no mail server.
///
/// Each test gets a database of its own. The reminders are counts and capped lists over
/// every open PM in the database, and the shared one holds stale PMs left by other tests,
/// so exact figures could not be asserted there and the lists could not be relied on to
/// contain this test's machine.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PmRemindersTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "PmReminders2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private string _suffix = null!;
    private string _database = null!;
    private string _connection = null!;
    private int _machineId;
    private int _scheduleId;

    private const string Tag = "RM-0001";

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

    private HospitalPmDbContext NewContext() => new(
        new DbContextOptionsBuilder<HospitalPmDbContext>().UseNpgsql(_connection).Options);

    public async Task InitializeAsync()
    {
        _suffix = Guid.NewGuid().ToString("N")[..8];
        _database = $"rm_{_suffix}";

        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_database}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        _connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = _database }.ToString();
        await using (var db = NewContext())
        {
            await db.Database.MigrateAsync();

            var room = new Location { Code = "RM-ROOM", Name = "Room RM", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();

            var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
            var machine = new Domain.Assets.Equipment { AssetTag = Tag, EquipmentTypeId = type.Id, LocationId = room.Id };
            var template = new ChecklistTemplate { EquipmentTypeId = type.Id, Code = "rm-pm", Name = "Reminder PM" };
            db.AddRange(machine, template);
            await db.SaveChangesAsync();
            _machineId = machine.Id;

            // Far in the future, so nothing else is generated for this machine.
            var schedule = new PmSchedule
            {
                EquipmentId = machine.Id, ChecklistTemplateId = template.Id,
                Frequency = PmFrequency.Yearly, AnchorDate = Today().AddYears(3), GraceDays = 7,
            };
            db.PmSchedules.Add(schedule);
            await db.SaveChangesAsync();
            _scheduleId = schedule.Id;

            db.PmTasks.AddRange(
                Task(-10, PmTaskStatus.Overdue),
                Task(-2, PmTaskStatus.Due),          // past its date but inside the grace
                Task(0, PmTaskStatus.Due),           // today
                Task(3, PmTaskStatus.Scheduled),
                Task(7, PmTaskStatus.Scheduled),     // the last day of the window
                Task(8, PmTaskStatus.Scheduled),     // one day too far
                Task(30, PmTaskStatus.Scheduled));   // well beyond
            await db.SaveChangesAsync();
        }

        _factory = new ApiFactory(_connection);
        _admin = await SignedInAsync("rm-admin", Roles.BmeHead);
        _employee = await SignedInAsync("rm-employee", Roles.BmeEngineer);
    }

    private PmTask Task(int daysFromToday, PmTaskStatus status) => new()
    {
        PmScheduleId = _scheduleId, EquipmentId = _machineId,
        DueDate = Today().AddDays(daysFromToday), Status = status,
    };

    private async Task<HttpClient> SignedInAsync(string userName, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = userName, FullName = userName, IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    public async Task DisposeAsync()
    {
        Dispose();

        // Best effort: a private database left behind is only clutter in a throwaway container.
        try
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(fixture.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
        catch (NpgsqlException)
        {
        }
    }

    public void Dispose()
    {
        _admin?.Dispose();
        _employee?.Dispose();
        _factory?.Dispose();
    }

    private static List<int> Days(JsonElement list) =>
        list.EnumerateArray().Select(i => i.GetProperty("daysFromToday").GetInt32()).ToList();

    [Fact]
    public async Task Everyone_signed_in_gets_the_same_reminders()
    {
        var admin = await _admin.GetFromJsonAsync<JsonElement>("/api/pm/reminders");
        var employee = await _employee.GetFromJsonAsync<JsonElement>("/api/pm/reminders");

        Assert.Equal(7, admin.GetProperty("leadDays").GetInt32());
        Assert.Equal(admin.ToString(), employee.ToString());
        Assert.Equal(1, employee.GetProperty("overdueCount").GetInt32());
        Assert.Equal(4, employee.GetProperty("upcomingCount").GetInt32());
    }

    [Fact]
    public async Task Somebody_who_is_not_signed_in_gets_nothing()
    {
        using var anonymous = _factory.CreateClient();

        var res = await anonymous.GetAsync("/api/pm/reminders");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task What_is_overdue_is_listed_on_its_own()
    {
        var body = await _employee.GetFromJsonAsync<JsonElement>("/api/pm/reminders");

        var item = Assert.Single(body.GetProperty("overdue").EnumerateArray());

        Assert.Equal(-10, item.GetProperty("daysFromToday").GetInt32());
        Assert.Equal(Today().AddDays(-10).ToString("yyyy-MM-dd"), item.GetProperty("dueDate").GetString());
        Assert.Equal((int)PmTaskStatus.Overdue, item.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task What_falls_due_within_seven_days_is_listed_soonest_first_and_nothing_later()
    {
        var body = await _employee.GetFromJsonAsync<JsonElement>("/api/pm/reminders");

        // Past its date but inside the grace, today, in three days, and on the seventh day.
        // Not the eighth, and not next month.
        Assert.Equal(new[] { -2, 0, 3, 7 }, Days(body.GetProperty("upcoming")));
    }

    [Fact]
    public async Task Each_reminder_says_which_machine_where_and_what()
    {
        var body = await _employee.GetFromJsonAsync<JsonElement>("/api/pm/reminders");

        var item = body.GetProperty("upcoming").EnumerateArray()
            .First(i => i.GetProperty("daysFromToday").GetInt32() == 3);

        Assert.Equal(Tag, item.GetProperty("assetTag").GetString());
        Assert.Equal(_machineId, item.GetProperty("equipmentId").GetInt32());
        Assert.Equal("Ventilator", item.GetProperty("equipmentTypeName").GetString());
        Assert.Equal("Room RM", item.GetProperty("locationName").GetString());
        Assert.Equal("Reminder PM", item.GetProperty("checklistName").GetString());
        Assert.True(item.GetProperty("taskId").GetInt32() > 0);
    }

    [Fact]
    public async Task A_PM_that_is_done_or_skipped_stops_being_a_reminder()
    {
        await using (var db = NewContext())
        {
            var signer = await db.Users.FirstAsync(u => u.UserName == "rm-admin");
            db.PmTasks.Add(new PmTask
            {
                PmScheduleId = _scheduleId, EquipmentId = _machineId,
                DueDate = Today().AddDays(2), Status = PmTaskStatus.Skipped,
                SkipReason = "Machine is away", CompletedAtUtc = DateTime.UtcNow, CompletedByUserId = signer.Id,
            });
            await db.SaveChangesAsync();
        }

        var body = await _employee.GetFromJsonAsync<JsonElement>("/api/pm/reminders");

        Assert.Equal(new[] { -2, 0, 3, 7 }, Days(body.GetProperty("upcoming")));
        Assert.Equal(4, body.GetProperty("upcomingCount").GetInt32());
    }

    [Fact]
    public async Task The_lists_are_capped_but_the_counts_are_not()
    {
        await using (var db = NewContext())
        {
            // Forty more overdue PMs, one a day going back from the day before.
            db.PmTasks.AddRange(Enumerable.Range(11, 40).Select(back => Task(-back, PmTaskStatus.Overdue)));
            await db.SaveChangesAsync();
        }

        var body = await _employee.GetFromJsonAsync<JsonElement>("/api/pm/reminders");

        Assert.Equal(41, body.GetProperty("overdueCount").GetInt32());
        var days = Days(body.GetProperty("overdue"));
        Assert.Equal(25, days.Count);

        // The most overdue first: fifty days back, then forty-nine, and so on.
        Assert.Equal(Enumerable.Range(0, 25).Select(n => -50 + n).ToList(), days);
    }

    [Fact]
    public async Task Nothing_to_remind_of_is_an_empty_answer_not_an_error()
    {
        await using (var db = NewContext())
        {
            await db.PmTasks.ExecuteDeleteAsync();
        }

        var body = await _employee.GetFromJsonAsync<JsonElement>("/api/pm/reminders");

        Assert.Equal(0, body.GetProperty("overdueCount").GetInt32());
        Assert.Equal(0, body.GetProperty("upcomingCount").GetInt32());
        Assert.Equal(0, body.GetProperty("overdue").GetArrayLength());
        Assert.Equal(0, body.GetProperty("upcoming").GetArrayLength());
    }
}
