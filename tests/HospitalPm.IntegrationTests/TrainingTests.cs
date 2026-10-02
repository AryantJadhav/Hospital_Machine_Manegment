using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Equipment;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>Training sessions and who attended: everyone reads, only an Admin writes.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class TrainingTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "Training2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private string _suffix = null!;
    private int _typeId;
    private int _otherTypeId;
    private int _staffUserId;
    private string _staffName = null!;
    private int _machineId;
    private int _secondMachineId;
    private string _machineTag = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var type = new EquipmentType { Code = $"tr-{_suffix}", Name = $"Training type {_suffix}" };
            var other = new EquipmentType { Code = $"tr2-{_suffix}", Name = $"Other type {_suffix}" };
            db.EquipmentTypes.AddRange(type, other);
            await db.SaveChangesAsync();
            _typeId = type.Id;
            _otherTypeId = other.Id;

            // Two machines of one make and model, told apart only by their numbers.
            var room = new Domain.Locations.Location
            {
                Code = $"TRL-{_suffix}", Name = $"Training room {_suffix}", Level = Domain.Locations.LocationLevel.Room,
            };
            db.Locations.Add(room);
            await db.SaveChangesAsync();

            _machineTag = $"TRM-{_suffix}-1".ToUpperInvariant();
            var first = new Domain.Assets.Equipment
            {
                AssetTag = _machineTag, EquipmentTypeId = type.Id, LocationId = room.Id,
                Manufacturer = $"Acme Medical {_suffix}", Model = $"V-300 {_suffix}", SerialNumber = $"SN-{_suffix}-1",
            };
            var second = new Domain.Assets.Equipment
            {
                AssetTag = $"TRM-{_suffix}-2".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
                Manufacturer = $"Acme Medical {_suffix}", Model = $"V-300 {_suffix}", SerialNumber = $"SN-{_suffix}-2",
            };
            db.Equipment.AddRange(first, second);
            await db.SaveChangesAsync();
            _machineId = first.Id;
            _secondMachineId = second.Id;
        }

        _admin = await SignInAsync("tr-admin", Domain.Identity.Roles.Admin);
        _employee = await SignInAsync("tr-emp", Domain.Identity.Roles.Employee);

        // A staff account, to be listed as an attendee by account rather than by name.
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
        _staffName = $"Staff Person {_suffix}";
        var staff = new Infrastructure.Identity.ApplicationUser
        {
            UserName = $"tr-staff-{_suffix}", FullName = _staffName, IsActive = true,
        };
        Assert.True((await users.CreateAsync(staff, Password)).Succeeded);
        await users.AddToRoleAsync(staff, Domain.Identity.Roles.Employee);
        _staffUserId = staff.Id;
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
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"{prefix}-{_suffix}", password = Password });
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
        _employee?.Dispose();
        _factory?.Dispose();
    }

    private static string Day(int daysFromToday) =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(daysFromToday)).ToString("yyyy-MM-dd");

    private object Session(
        string title,
        string? date = null,
        int? typeId = null,
        string? trainer = null,
        object[]? attendees = null,
        int? minutes = null,
        int? machineId = null) => new
    {
        title = $"{title} {_suffix}",
        sessionDate = date ?? Day(-3),
        equipmentTypeId = typeId,
        equipmentId = machineId,
        trainer,
        durationMinutes = minutes,
        attendees = attendees ?? [],
    };

    private async Task<int> CreateAsync(object body)
    {
        var res = await _admin.PostAsJsonAsync("/api/training", body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> GetAsync(int id) => await _employee.GetFromJsonAsync<JsonElement>($"/api/training/{id}");

    private async Task<HashSet<int>> ListIdsAsync(string query)
    {
        var list = await _employee.GetFromJsonAsync<JsonElement>($"/api/training?{query}&pageSize=200");
        return list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToHashSet();
    }

    [Fact]
    public async Task An_admin_adds_a_session_with_its_attendees_and_an_employee_reads_it()
    {
        var id = await CreateAsync(Session(
            "Ventilator alarms",
            typeId: _typeId,
            trainer: "Dr Rao, Acme Medical",
            minutes: 90,
            attendees:
            [
                new { userId = _staffUserId },
                new { name = "Nurse Meera", designation = "Staff nurse, ICU" },
            ]));

        var seen = await GetAsync(id);

        Assert.Equal($"Ventilator alarms {_suffix}", seen.GetProperty("title").GetString());
        Assert.Equal($"Training type {_suffix}", seen.GetProperty("equipmentTypeName").GetString());
        Assert.Equal("Dr Rao, Acme Medical", seen.GetProperty("trainer").GetString());
        Assert.Equal(90, seen.GetProperty("durationMinutes").GetInt32());
        Assert.False(seen.GetProperty("isPlanned").GetBoolean());

        var people = seen.GetProperty("attendees").EnumerateArray().ToList();
        Assert.Equal(2, people.Count);
        Assert.Contains(people, p => p.GetProperty("name").GetString() == _staffName
            && p.GetProperty("userId").GetInt32() == _staffUserId);
        Assert.Contains(people, p => p.GetProperty("name").GetString() == "Nurse Meera"
            && p.GetProperty("designation").GetString() == "Staff nurse, ICU");
    }

    [Fact]
    public async Task A_session_needs_no_title_and_is_then_simply_a_training_session()
    {
        var bare = await _admin.PostAsJsonAsync("/api/training", new { sessionDate = Day(-1), trainer = $"Bare {_suffix}" });
        Assert.Equal(HttpStatusCode.Created, bare.StatusCode);
        var id = (await bare.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var seen = await GetAsync(id);
        Assert.Equal("Training session", seen.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("equipmentTypeId").ValueKind);

        // A blank one is the same as none.
        var blank = await _admin.PostAsJsonAsync("/api/training", new { title = "   ", sessionDate = Day(-1) });
        Assert.Equal(HttpStatusCode.Created, blank.StatusCode);
    }

    [Fact]
    public async Task A_session_takes_the_machines_name_company_and_model_from_the_machines_own_record()
    {
        var id = await CreateAsync(Session("On a machine", machineId: _machineId));

        var detail = await GetAsync(id);
        var machine = detail.GetProperty("machine");

        // The reference the printed report carries, so the screen and the paper quote the same thing.
        var year = DateOnly.Parse(detail.GetProperty("sessionDate").GetString()!, System.Globalization.CultureInfo.InvariantCulture).Year;
        Assert.Equal($"TR-{year}-{id:D5}", detail.GetProperty("reference").GetString());

        Assert.Equal(_machineTag, machine.GetProperty("assetTag").GetString());
        Assert.Equal($"Training type {_suffix}", machine.GetProperty("machineName").GetString());
        Assert.Equal($"Acme Medical {_suffix}", machine.GetProperty("manufacturer").GetString());
        Assert.Equal($"V-300 {_suffix}", machine.GetProperty("model").GetString());
        Assert.Equal($"SN-{_suffix}-1", machine.GetProperty("serialNumber").GetString());
        Assert.Equal($"Training room {_suffix}", machine.GetProperty("locationName").GetString());
    }

    [Fact]
    public async Task Choosing_a_machine_also_records_its_kind_so_the_two_cannot_disagree()
    {
        // Asked for the other kind of machine, but the machine chosen is of the first.
        var id = await CreateAsync(Session("Disagree", typeId: _otherTypeId, machineId: _machineId));

        Assert.Equal(_typeId, (await GetAsync(id)).GetProperty("equipmentTypeId").GetInt32());
    }

    [Fact]
    public async Task An_unknown_machine_is_refused()
    {
        var res = await _admin.PostAsJsonAsync("/api/training", Session("Ghost machine", machineId: 99999999));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Sessions_are_found_by_the_machines_number_company_or_model_and_the_list_names_the_machine()
    {
        var onFirst = await CreateAsync(Session("First machine", machineId: _machineId));
        var onSecond = await CreateAsync(Session("Second machine", machineId: _secondMachineId));
        var none = await CreateAsync(Session("No machine"));

        var byNumber = await ListIdsAsync($"q={_machineTag}");
        Assert.Contains(onFirst, byNumber);
        Assert.DoesNotContain(onSecond, byNumber);

        // Company and model are shared by both machines, so both sessions are found, and not the one with none.
        foreach (var term in new[] { $"Acme Medical {_suffix}", $"v-300 {_suffix}" })
        {
            var ids = await ListIdsAsync($"q={Uri.EscapeDataString(term)}");
            Assert.Contains(onFirst, ids);
            Assert.Contains(onSecond, ids);
            Assert.DoesNotContain(none, ids);
        }

        var list = await _employee.GetFromJsonAsync<JsonElement>($"/api/training?q={_machineTag}");
        var item = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == onFirst);
        Assert.Equal(_machineTag, item.GetProperty("assetTag").GetString());
        Assert.Equal($"V-300 {_suffix}", item.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Editing_can_move_a_session_to_another_machine_or_take_the_machine_off()
    {
        var id = await CreateAsync(Session("Move me", machineId: _machineId));

        (await _admin.PutAsJsonAsync($"/api/training/{id}", Session("Move me", machineId: _secondMachineId))).EnsureSuccessStatusCode();
        Assert.Equal($"SN-{_suffix}-2", (await GetAsync(id)).GetProperty("machine").GetProperty("serialNumber").GetString());

        (await _admin.PutAsJsonAsync($"/api/training/{id}", Session("Move me"))).EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null, (await GetAsync(id)).GetProperty("machine").ValueKind);
    }

    [Fact]
    public async Task The_training_report_is_a_pdf_anyone_signed_in_can_open()
    {
        var id = await CreateAsync(Session(
            "Reported on",
            machineId: _machineId,
            trainer: "Dr Rao",
            minutes: 60,
            attendees: [new { userId = _staffUserId }, new { name = "Nurse Meera", designation = "ICU" }]));

        foreach (var client in new[] { _admin, _employee })
        {
            var res = await client.GetAsync($"/api/training/{id}/report.pdf");

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
            var bytes = await res.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.Length > 1000);
            Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
        }
    }

    [Fact]
    public async Task The_report_still_prints_for_a_session_with_no_machine_no_people_or_a_date_to_come()
    {
        var bare = await CreateAsync(Session("Bare"));
        var planned = await CreateAsync(Session("Planned", date: Day(20), machineId: _machineId));

        foreach (var id in new[] { bare, planned })
        {
            var res = await _employee.GetAsync($"/api/training/{id}/report.pdf");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
    }

    [Fact]
    public async Task A_report_for_a_session_that_does_not_exist_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _employee.GetAsync("/api/training/2000000000/report.pdf")).StatusCode);
    }

    [Fact]
    public async Task A_session_in_the_future_is_marked_as_planned()
    {
        var id = await CreateAsync(Session("Pump basics", date: Day(10)));

        Assert.True((await GetAsync(id)).GetProperty("isPlanned").GetBoolean());
    }

    [Fact]
    public async Task An_employee_cannot_add_change_or_remove_a_session()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _employee.PostAsJsonAsync("/api/training", Session("No"))).StatusCode);

        var id = await CreateAsync(Session("Admin's session"));

        Assert.Equal(HttpStatusCode.Forbidden, (await _employee.PutAsJsonAsync($"/api/training/{id}", Session("Changed"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _employee.DeleteAsync($"/api/training/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_session_with_an_absurd_date_or_length_or_a_too_long_title_is_refused()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync("/api/training", Session("Old", date: "1990-01-01"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _admin.PostAsJsonAsync("/api/training", Session("x").With("title", new string('x', 201)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync("/api/training", Session("Far", date: "2200-01-01"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync("/api/training", Session("Zero", minutes: 0))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync("/api/training", Session("Days", minutes: 5000))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync("/api/training", Session("Type", typeId: 99999999))).StatusCode);
    }

    [Fact]
    public async Task The_same_person_cannot_be_listed_twice_and_everyone_needs_a_name()
    {
        var twiceByAccount = await _admin.PostAsJsonAsync("/api/training",
            Session("Dup 1", attendees: [new { userId = _staffUserId }, new { userId = _staffUserId }]));
        Assert.Equal(HttpStatusCode.BadRequest, twiceByAccount.StatusCode);

        var twiceByName = await _admin.PostAsJsonAsync("/api/training",
            Session("Dup 2", attendees: [new { name = "Asha Rao" }, new { name = "asha rao" }]));
        Assert.Equal(HttpStatusCode.BadRequest, twiceByName.StatusCode);

        var noName = await _admin.PostAsJsonAsync("/api/training", Session("No name", attendees: [new { name = "  " }]));
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);

        var unknownUser = await _admin.PostAsJsonAsync("/api/training", Session("Ghost", attendees: [new { userId = 99999999 }]));
        Assert.Equal(HttpStatusCode.BadRequest, unknownUser.StatusCode);
    }

    [Fact]
    public async Task Editing_a_session_replaces_its_attendees()
    {
        var id = await CreateAsync(Session("Edit me", attendees: [new { name = "Ravi" }, new { name = "Asha" }]));

        var edit = await _admin.PutAsJsonAsync($"/api/training/{id}",
            Session("Edit me", date: Day(-1), typeId: _typeId, attendees: [new { name = "Asha" }, new { name = "Meera" }]));
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);

        var seen = await GetAsync(id);
        var names = seen.GetProperty("attendees").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();

        Assert.Equal(["Asha", "Meera"], names);
        Assert.Equal(Day(-1), seen.GetProperty("sessionDate").GetString());
        Assert.Equal($"Training type {_suffix}", seen.GetProperty("equipmentTypeName").GetString());
    }

    [Fact]
    public async Task Removing_a_session_removes_its_attendees_with_it()
    {
        var id = await CreateAsync(Session("Remove me", attendees: [new { name = "Someone" }]));

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/training/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _employee.GetAsync($"/api/training/{id}")).StatusCode);

        await using var db = fixture.CreateContext();
        Assert.False(await db.TrainingAttendees.AnyAsync(a => a.TrainingSessionId == id));
    }

    [Fact]
    public async Task A_session_that_does_not_exist_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _employee.GetAsync("/api/training/2000000000")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.PutAsJsonAsync("/api/training/2000000000", Session("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.DeleteAsync("/api/training/2000000000")).StatusCode);
    }

    [Fact]
    public async Task The_list_is_newest_first_and_can_be_searched_by_title_trainer_or_attendee()
    {
        var older = await CreateAsync(Session("Searchable older", date: Day(-20), trainer: $"Trainer Zed {_suffix}"));
        var newer = await CreateAsync(Session("Searchable newer", date: Day(-2), attendees: [new { name = $"Attendee Quill {_suffix}" }]));

        var all = await _employee.GetFromJsonAsync<JsonElement>($"/api/training?q=Searchable&pageSize=200");
        var order = all.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetInt32()).Where(i => i == older || i == newer).ToList();
        Assert.Equal([newer, older], order);

        Assert.Contains(older, await ListIdsAsync($"q=Trainer Zed {_suffix}"));
        Assert.DoesNotContain(newer, await ListIdsAsync($"q=Trainer Zed {_suffix}"));
        Assert.Contains(newer, await ListIdsAsync($"q=attendee quill {_suffix}"));
        Assert.DoesNotContain(older, await ListIdsAsync($"q=attendee quill {_suffix}"));
    }

    [Fact]
    public async Task The_list_can_be_narrowed_to_a_kind_of_machine_and_a_range_of_dates()
    {
        var onType = await CreateAsync(Session("Filter A", date: Day(-5), typeId: _typeId));
        var onOther = await CreateAsync(Session("Filter B", date: Day(-5), typeId: _otherTypeId));
        var oldOne = await CreateAsync(Session("Filter C", date: Day(-60), typeId: _typeId));

        var byType = await ListIdsAsync($"equipmentTypeId={_typeId}");
        Assert.Contains(onType, byType);
        Assert.DoesNotContain(onOther, byType);

        var byDate = await ListIdsAsync($"equipmentTypeId={_typeId}&from={Day(-10)}&to={Day(0)}");
        Assert.Contains(onType, byDate);
        Assert.DoesNotContain(oldOne, byDate);
    }

    [Fact]
    public async Task The_list_shows_how_many_came_and_the_first_few_names()
    {
        var id = await CreateAsync(Session("Counted", attendees:
            [new { name = "D" }, new { name = "B" }, new { name = "A" }, new { name = "C" }, new { name = "E" }]));

        var list = await _employee.GetFromJsonAsync<JsonElement>($"/api/training?q=Counted {_suffix}");
        var item = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == id);

        Assert.Equal(5, item.GetProperty("attendeeCount").GetInt32());
        Assert.Equal(["A", "B", "C"], item.GetProperty("someAttendees").EnumerateArray().Select(n => n.GetString()!).ToArray());
    }

    [Fact]
    public async Task The_people_view_counts_each_person_once_however_their_name_was_written()
    {
        var who = $"Pooja Verma {_suffix}";
        await CreateAsync(Session("People 1", date: Day(-30), typeId: _typeId, attendees: [new { name = who, designation = "Technician" }]));
        await CreateAsync(Session("People 2", date: Day(-4), typeId: _otherTypeId, attendees: [new { name = who.ToUpperInvariant() }]));
        await CreateAsync(Session("People 3", date: Day(-9), attendees: [new { userId = _staffUserId }]));

        var people = await _employee.GetFromJsonAsync<JsonElement>($"/api/training/people?q={_suffix}");
        var list = people.EnumerateArray().ToList();

        var pooja = list.Single(p => string.Equals(p.GetProperty("name").GetString(), who, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, pooja.GetProperty("sessions").GetInt32());
        Assert.Equal(Day(-4), pooja.GetProperty("lastSessionDate").GetString());
        Assert.Equal("Technician", pooja.GetProperty("designation").GetString());
        Assert.Equal(2, pooja.GetProperty("covered").GetArrayLength());

        var staff = list.Single(p => p.GetProperty("name").GetString() == _staffName);
        Assert.Equal(1, staff.GetProperty("sessions").GetInt32());
    }

    [Fact]
    public async Task A_session_is_recorded_in_the_audit_log()
    {
        var id = await CreateAsync(Session("Audited"));

        await using var db = fixture.CreateContext();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE table_name = 'training_session' AND record_pk = @pk", conn);
        cmd.Parameters.AddWithValue("pk", id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public async Task The_database_itself_refuses_a_blank_title_or_a_zero_length_session()
    {
        await using var db = fixture.CreateContext();

        var blank = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO training_session (tenant_id, title, session_date, created_by_user_id) VALUES (1, '   ', current_date, 1)"));
        Assert.Equal("ck_training_session_title", blank.ConstraintName);

        var zero = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO training_session (tenant_id, title, session_date, duration_minutes, created_by_user_id) VALUES (1, 'x', current_date, 0, 1)"));
        Assert.Equal("ck_training_session_duration", zero.ConstraintName);
    }
}

internal static class AnonymousExtensions
{
    /// <summary>The same body with one property replaced, for the one test that needs a title the helper would otherwise decorate.</summary>
    public static Dictionary<string, object?> With(this object body, string key, object? value)
    {
        var json = JsonSerializer.Serialize(body);
        var map = JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!;
        map[key] = value;
        return map;
    }
}
