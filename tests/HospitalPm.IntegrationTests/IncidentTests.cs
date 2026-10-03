using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Incidents with machines: a department writes one up, the biomedical team looks into it and closes it,
/// and a person from another department sees only the incidents on their own departments' machines.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class IncidentTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "Incident2026!";

    private ApiFactory _factory = null!;
    private string _suffix = null!;

    private int _icuDepartmentId;
    private int _icuRoomId;
    private int _otRoomId;
    private int _icuMachineId;
    private int _icuSecondMachineId;
    private int _otMachineId;
    private string _icuTag = null!;
    private string _otTag = null!;

    private HttpClient _head = null!;
    private HttpClient _engineer = null!;
    private HttpClient _it = null!;
    private HttpClient _ward = null!;
    private int _engineerId;
    private int _wardId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var building = new Location { Code = $"INB-{_suffix}", Name = $"Block {_suffix}", Level = LocationLevel.Building };
            db.Locations.Add(building);
            await db.SaveChangesAsync();

            var icu = new Location { Code = $"INI-{_suffix}", Name = $"ICU {_suffix}", Level = LocationLevel.Department, ParentId = building.Id };
            var ot = new Location { Code = $"INO-{_suffix}", Name = $"OT {_suffix}", Level = LocationLevel.Department, ParentId = building.Id };
            db.Locations.AddRange(icu, ot);
            await db.SaveChangesAsync();

            var icuRoom = new Location { Code = $"INIR-{_suffix}", Name = $"ICU bay {_suffix}", Level = LocationLevel.Room, ParentId = icu.Id };
            var otRoom = new Location { Code = $"INOR-{_suffix}", Name = $"OT theatre {_suffix}", Level = LocationLevel.Room, ParentId = ot.Id };
            db.Locations.AddRange(icuRoom, otRoom);
            await db.SaveChangesAsync();

            _icuDepartmentId = icu.Id;
            _icuRoomId = icuRoom.Id;
            _otRoomId = otRoom.Id;

            var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
            _icuTag = $"INIM-{_suffix}".ToUpperInvariant();
            _otTag = $"INOM-{_suffix}".ToUpperInvariant();

            var icuMachine = new Domain.Assets.Equipment
            {
                AssetTag = _icuTag, EquipmentTypeId = type.Id, LocationId = icuRoom.Id, Manufacturer = "Acme", Model = "V-1",
            };
            var icuSecond = new Domain.Assets.Equipment
            {
                AssetTag = $"ININ-{_suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = icuRoom.Id,
            };
            var otMachine = new Domain.Assets.Equipment
            {
                AssetTag = _otTag, EquipmentTypeId = type.Id, LocationId = otRoom.Id,
            };
            db.Equipment.AddRange(icuMachine, icuSecond, otMachine);
            await db.SaveChangesAsync();
            _icuMachineId = icuMachine.Id;
            _icuSecondMachineId = icuSecond.Id;
            _otMachineId = otMachine.Id;
        }

        (_head, _) = await SignInAsync("in-head", Roles.BmeHead);
        (_engineer, _engineerId) = await SignInAsync("in-eng", Roles.BmeEngineer);
        (_it, _) = await SignInAsync("in-it", Roles.ItAdmin);
        (_ward, _wardId) = await SignInAsync("in-ward", Roles.DepartmentUser);

        // The ward user belongs to the ICU, and so to everything beneath it.
        Assert.Equal(HttpStatusCode.NoContent,
            (await _head.PutAsJsonAsync($"/api/users/{_wardId}/departments", new { locationIds = new[] { _icuDepartmentId } })).StatusCode);
    }

    private async Task<(HttpClient Client, int UserId)> SignInAsync(string prefix, string role)
    {
        int userId;
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
            userId = user.Id;
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = $"{prefix}-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (client, userId);
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _head?.Dispose();
        _engineer?.Dispose();
        _it?.Dispose();
        _ward?.Dispose();
        _factory?.Dispose();
    }

    private static string Day(int daysFromToday) =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(daysFromToday)).ToString("yyyy-MM-dd");

    // 10 fall, 20 mishandling, 30 liquid, 40 collision, 50 missing, 90 other. 10 none, 20 minor, 30 major, 40 beyond repair.
    private object Incident(
        int? machineId = null,
        int type = 10,
        string? description = null,
        string? occurredOn = null,
        string? occurredAt = null,
        int? damage = null,
        string? place = null,
        bool? takenOutOfUse = null,
        string? involved = null) => new
    {
        equipmentId = machineId ?? _icuMachineId,
        type,
        occurredOn,
        occurredAt,
        place,
        description = description ?? $"Dropped while being moved {_suffix}",
        involvedPerson = involved,
        immediateAction = "Switched off and sent to the biomedical store",
        takenOutOfUse,
        damage,
    };

    private async Task<int> ReportAsync(object body, HttpClient? by = null)
    {
        var res = await (by ?? _engineer).PostAsJsonAsync("/api/incidents", body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> GetAsync(int id, HttpClient? as_ = null)
    {
        var res = await (as_ ?? _engineer).GetAsync($"/api/incidents/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> ListAsync(string query, HttpClient? as_ = null)
    {
        var res = await (as_ ?? _engineer).GetAsync($"/api/incidents?{query}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> ErrorOf(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    private static List<int> Ids(JsonElement list) =>
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();

    // ---------------------------------------------------------------- who may

    [Fact]
    public async Task The_biomedical_team_and_a_ward_write_incidents_but_the_it_team_sees_none()
    {
        var byEngineer = await ReportAsync(Incident());
        var byHead = await ReportAsync(Incident(), _head);
        var byWard = await ReportAsync(Incident(), _ward);

        Assert.Equal(HttpStatusCode.OK, (await _engineer.GetAsync($"/api/incidents/{byHead}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _head.GetAsync($"/api/incidents/{byEngineer}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _ward.GetAsync($"/api/incidents/{byWard}")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await _it.GetAsync("/api/incidents")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _it.GetAsync($"/api/incidents/{byEngineer}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _it.GetAsync($"/api/incidents/{byEngineer}/report.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _it.GetAsync("/api/incidents/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _it.PostAsJsonAsync("/api/incidents", Incident())).StatusCode);
    }

    [Fact]
    public async Task A_ward_cannot_review_or_close_what_it_reports()
    {
        var id = await ReportAsync(Incident(), _ward);

        Assert.Equal(HttpStatusCode.Forbidden, (await _ward.PutAsJsonAsync($"/api/incidents/{id}", Incident())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _ward.PutAsJsonAsync($"/api/incidents/{id}/review", new { findings = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _ward.PostAsJsonAsync($"/api/incidents/{id}/close", new { findings = "x" })).StatusCode);
        Assert.Equal("Reported", (await GetAsync(id)).GetProperty("status").GetString());
    }

    // ---------------------------------------------------------------- writing one

    [Fact]
    public async Task An_incident_is_written_up_with_what_was_said_and_where_the_machine_was()
    {
        var id = await ReportAsync(Incident(
            type: 10, occurredOn: Day(-1), occurredAt: "14:30", damage: 30, place: "Bay 4, off the bedside table",
            takenOutOfUse: true, involved: "Staff nurse, ICU"));

        var i = await GetAsync(id);
        Assert.Matches(@"^INC-\d{4}-\d{5}$", i.GetProperty("reference").GetString());
        Assert.Equal(id, int.Parse(i.GetProperty("reference").GetString()![^5..]));
        Assert.Equal("Reported", i.GetProperty("status").GetString());
        Assert.Equal("Fall or drop", i.GetProperty("typeLabel").GetString());
        Assert.Equal(Day(-1), i.GetProperty("occurredOn").GetString());
        Assert.Equal("14:30", i.GetProperty("occurredAt").GetString());
        Assert.Equal("Bay 4, off the bedside table", i.GetProperty("place").GetString());
        Assert.Equal(30, i.GetProperty("damage").GetInt32());
        Assert.Equal("Major damage, needs repair", i.GetProperty("damageLabel").GetString());
        Assert.True(i.GetProperty("takenOutOfUse").GetBoolean());
        Assert.Equal("Staff nurse, ICU", i.GetProperty("involvedPerson").GetString());
        Assert.Equal("in-eng person", i.GetProperty("reportedByName").GetString());
        Assert.Equal(_icuTag, i.GetProperty("machine").GetProperty("assetTag").GetString());
        Assert.Equal($"ICU bay {_suffix}", i.GetProperty("locationName").GetString());
        Assert.Equal(JsonValueKind.Null, i.GetProperty("closedAtUtc").ValueKind);
    }

    [Fact]
    public async Task What_is_left_out_is_today_and_no_visible_damage()
    {
        var i = await GetAsync(await ReportAsync(Incident()));

        Assert.Equal(Day(0), i.GetProperty("occurredOn").GetString());
        Assert.Equal(10, i.GetProperty("damage").GetInt32());
        Assert.False(i.GetProperty("takenOutOfUse").GetBoolean());
        Assert.Equal(JsonValueKind.Null, i.GetProperty("occurredAt").ValueKind);
    }

    [Fact]
    public async Task An_incident_that_makes_no_sense_is_refused_with_the_reason()
    {
        async Task Refused(object body, string part)
        {
            var res = await _engineer.PostAsJsonAsync("/api/incidents", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Contains(part, await ErrorOf(res), StringComparison.OrdinalIgnoreCase);
        }

        await Refused(Incident(description: "  "), "describe what happened");
        await Refused(Incident(type: 7), "what happened");
        await Refused(Incident(damage: 99), "state the machine");
        await Refused(Incident(occurredOn: Day(3)), "not come yet");
        await Refused(Incident(occurredAt: "25:99"), "hours and minutes");
        await Refused(Incident(occurredAt: "2pm"), "hours and minutes");
        await Refused(Incident(machineId: 2_000_000_000), "choose the machine");
        await Refused(Incident(description: new string('x', 4001)), "at most");
        await Refused(Incident(place: new string('x', 201)), "at most");
    }

    [Fact]
    public async Task The_place_is_kept_as_it_was_when_the_machine_is_moved_afterwards()
    {
        var id = await ReportAsync(Incident());

        await using (var db = fixture.CreateContext())
        {
            var machine = db.Equipment.Single(e => e.Id == _icuMachineId);
            machine.LocationId = _otRoomId;
            await db.SaveChangesAsync();
        }

        Assert.Equal($"ICU bay {_suffix}", (await GetAsync(id)).GetProperty("locationName").GetString());
    }

    // ---------------------------------------------------------------- a ward sees its own

    [Fact]
    public async Task A_ward_reports_on_its_own_machines_and_not_on_another_departments()
    {
        await ReportAsync(Incident(machineId: _icuMachineId), _ward);

        var other = await _ward.PostAsJsonAsync("/api/incidents", Incident(machineId: _otMachineId));
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        Assert.Contains("choose the machine", await ErrorOf(other), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_ward_sees_the_incidents_on_its_own_departments_machines_and_none_elsewhere()
    {
        var icu = await ReportAsync(Incident(machineId: _icuMachineId, description: $"ICU one {_suffix}"));
        var ot = await ReportAsync(Incident(machineId: _otMachineId, description: $"OT one {_suffix}"));

        // What the biomedical team sees: both.
        Assert.Equal(new[] { icu, ot }.Order(), Ids(await ListAsync($"q={_suffix}")).Order());

        // What the ward sees: its own, and the other is not there at all.
        Assert.Equal([icu], Ids(await ListAsync($"q={_suffix}", _ward)));
        Assert.Equal(HttpStatusCode.OK, (await _ward.GetAsync($"/api/incidents/{icu}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _ward.GetAsync($"/api/incidents/{ot}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _ward.GetAsync($"/api/incidents/{ot}/report.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _ward.GetAsync($"/api/incidents/{icu}/report.pdf")).StatusCode);

        // Nor can it be reached by asking for that machine's incidents, or by searching for what it says.
        Assert.Empty(Ids(await ListAsync($"equipmentId={_otMachineId}", _ward)));
        Assert.Empty(Ids(await ListAsync($"q={Uri.EscapeDataString("OT one " + _suffix)}", _ward)));
    }

    [Fact]
    public async Task What_a_ward_is_counted_and_printed_covers_only_its_own_departments()
    {
        await ReportAsync(Incident(machineId: _icuMachineId, type: 10));
        await ReportAsync(Incident(machineId: _otMachineId, type: 20));
        await ReportAsync(Incident(machineId: _otMachineId, type: 20));

        var ward = await (await _ward.GetAsync("/api/incidents/summary")).Content.ReadFromJsonAsync<JsonElement>();
        var all = await (await _engineer.GetAsync("/api/incidents/summary")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(all.GetProperty("total").GetInt32() >= ward.GetProperty("total").GetInt32() + 2);
        // Not one place of the other department is named in what the ward is told.
        Assert.DoesNotContain($"OT theatre {_suffix}", ward.GetRawText());
        Assert.DoesNotContain(_otTag, ward.GetRawText());
    }

    // ---------------------------------------------------------------- looking into it

    [Fact]
    public async Task Looking_into_it_moves_it_on_and_the_damage_can_be_corrected()
    {
        var id = await ReportAsync(Incident(damage: 10), _ward);

        var review = await _engineer.PutAsJsonAsync($"/api/incidents/{id}/review", new { findings = (string?)null, correctiveAction = (string?)null, damage = 30 });
        Assert.Equal(HttpStatusCode.NoContent, review.StatusCode);

        var i = await GetAsync(id);
        Assert.Equal("InReview", i.GetProperty("status").GetString());
        Assert.Equal("Under review", i.GetProperty("statusLabel").GetString());
        Assert.Equal(30, i.GetProperty("damage").GetInt32());
        Assert.Equal(JsonValueKind.Null, i.GetProperty("findings").ValueKind);
    }

    [Fact]
    public async Task An_incident_cannot_be_closed_without_saying_what_caused_it()
    {
        var id = await ReportAsync(Incident());

        var refused = await _engineer.PostAsJsonAsync($"/api/incidents/{id}/close", new { });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("what caused it", await ErrorOf(refused), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Reported", (await GetAsync(id)).GetProperty("status").GetString());

        // Said while closing, in the one call.
        var closed = await _engineer.PostAsJsonAsync($"/api/incidents/{id}/close",
            new { findings = "Trolley brake was not locked", correctiveAction = "Trolleys checked; brake check added to the round" });
        Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);

        var i = await GetAsync(id);
        Assert.Equal("Closed", i.GetProperty("status").GetString());
        Assert.Equal("Trolley brake was not locked", i.GetProperty("findings").GetString());
        Assert.Equal("Trolleys checked; brake check added to the round", i.GetProperty("correctiveAction").GetString());
        Assert.Equal("in-eng person", i.GetProperty("closedByName").GetString());
        Assert.NotEqual(JsonValueKind.Null, i.GetProperty("closedAtUtc").ValueKind);
    }

    [Fact]
    public async Task What_was_found_can_be_written_over_several_visits_before_closing()
    {
        var id = await ReportAsync(Incident());

        await _engineer.PutAsJsonAsync($"/api/incidents/{id}/review", new { findings = "Looks like the stand", correctiveAction = (string?)null, damage = (int?)null });
        await _head.PutAsJsonAsync($"/api/incidents/{id}/review", new { findings = "Confirmed: the stand's clamp was loose", correctiveAction = "Replace the clamp", damage = (int?)null });
        Assert.Equal(HttpStatusCode.NoContent, (await _head.PostAsJsonAsync($"/api/incidents/{id}/close", new { })).StatusCode);

        var i = await GetAsync(id);
        Assert.Equal("Confirmed: the stand's clamp was loose", i.GetProperty("findings").GetString());
        Assert.Equal("Replace the clamp", i.GetProperty("correctiveAction").GetString());
    }

    [Fact]
    public async Task A_closed_incident_is_final()
    {
        var id = await ReportAsync(Incident());
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/incidents/{id}/close", new { findings = "Rough handling" })).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await _engineer.PutAsJsonAsync($"/api/incidents/{id}", Incident())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _engineer.PutAsJsonAsync($"/api/incidents/{id}/review", new { findings = "Changed my mind" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _engineer.PostAsJsonAsync($"/api/incidents/{id}/close", new { findings = "Again" })).StatusCode);

        Assert.Equal("Rough handling", (await GetAsync(id)).GetProperty("findings").GetString());
    }

    [Fact]
    public async Task What_was_closed_cannot_be_changed_even_by_the_database_directly()
    {
        var id = await ReportAsync(Incident());
        await _engineer.PostAsJsonAsync($"/api/incidents/{id}/close", new { findings = "Rough handling" });

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();

        foreach (var sql in new[]
        {
            "UPDATE incident SET description = 'Rewritten' WHERE id = @id",
            "UPDATE incident SET status = 20, closed_at_utc = NULL WHERE id = @id",
            "UPDATE incident SET findings = 'Other' WHERE id = @id",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", id);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal("23514", ex.SqlState);
        }
    }

    [Fact]
    public async Task The_database_will_not_hold_a_closed_incident_that_says_nothing_about_the_cause()
    {
        var id = await ReportAsync(Incident());

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE incident SET status = 30, closed_at_utc = now() WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task The_biomedical_team_can_correct_what_was_written_up_until_it_is_closed()
    {
        var id = await ReportAsync(Incident(machineId: _icuMachineId, type: 10), _ward);

        var edit = await _engineer.PutAsJsonAsync($"/api/incidents/{id}",
            Incident(machineId: _icuSecondMachineId, type: 20, description: $"Wrong machine was named {_suffix}", damage: 20));
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);

        var i = await GetAsync(id);
        Assert.Equal(_icuSecondMachineId, i.GetProperty("equipmentId").GetInt32());
        Assert.Equal("Mishandling", i.GetProperty("typeLabel").GetString());
        Assert.Equal(20, i.GetProperty("damage").GetInt32());
        Assert.Equal("in-ward person", i.GetProperty("reportedByName").GetString());
    }

    [Fact]
    public async Task There_is_no_way_to_delete_an_incident()
    {
        var id = await ReportAsync(Incident());

        var res = await _head.DeleteAsync($"/api/incidents/{id}");
        Assert.True(res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.Equal(HttpStatusCode.OK, (await _head.GetAsync($"/api/incidents/{id}")).StatusCode);
    }

    // ---------------------------------------------------------------- the list

    [Fact]
    public async Task The_list_puts_what_needs_looking_at_first_and_the_oldest_of_those_at_the_top()
    {
        var closed = await ReportAsync(Incident(description: $"Closed one {_suffix}"));
        await _engineer.PostAsJsonAsync($"/api/incidents/{closed}/close", new { findings = "Done" });
        var first = await ReportAsync(Incident(description: $"First one {_suffix}"));
        var second = await ReportAsync(Incident(description: $"Second one {_suffix}"));

        Assert.Equal([first, second, closed], Ids(await ListAsync($"q={_suffix}")));

        var counts = (await ListAsync($"q={_suffix}")).GetProperty("counts");
        Assert.Equal(2, counts.GetProperty("reported").GetInt32());
        Assert.Equal(0, counts.GetProperty("inReview").GetInt32());
        Assert.Equal(1, counts.GetProperty("closed").GetInt32());
    }

    [Fact]
    public async Task The_list_can_be_narrowed_by_status_kind_machine_and_whose_it_is()
    {
        var fall = await ReportAsync(Incident(type: 10, machineId: _icuMachineId));
        var spill = await ReportAsync(Incident(type: 30, machineId: _icuSecondMachineId), _ward);
        await _engineer.PutAsJsonAsync($"/api/incidents/{spill}/review", new { });
        var closed = await ReportAsync(Incident(type: 10, machineId: _icuMachineId));
        await _engineer.PostAsJsonAsync($"/api/incidents/{closed}/close", new { findings = "Done" });

        Assert.Equal(new[] { fall, spill }.Order(), Ids(await ListAsync($"q={_suffix}&status=open")).Order());
        Assert.Equal([fall], Ids(await ListAsync($"q={_suffix}&status=reported")));
        Assert.Equal([spill], Ids(await ListAsync($"q={_suffix}&status=review")));
        Assert.Equal([closed], Ids(await ListAsync($"q={_suffix}&status=closed")));

        Assert.Equal(new[] { fall, closed }.Order(), Ids(await ListAsync($"q={_suffix}&type=10")).Order());
        Assert.Equal([spill], Ids(await ListAsync($"q={_suffix}&type=30")));
        Assert.Equal([spill], Ids(await ListAsync($"q={_suffix}&equipmentId={_icuSecondMachineId}")));

        // Mine: what this person wrote, and no one else's.
        Assert.Equal([spill], Ids(await ListAsync($"q={_suffix}&mine=true", _ward)));
        Assert.Equal(new[] { fall, closed }.Order(), Ids(await ListAsync($"q={_suffix}&mine=true")).Order());
    }

    [Fact]
    public async Task An_incident_is_found_by_its_number_what_was_said_or_the_machine()
    {
        var id = await ReportAsync(Incident(description: $"Wheel came off the stand {_suffix}", place: $"Corridor {_suffix}"));

        async Task<List<int>> Found(string q) => Ids(await ListAsync($"q={Uri.EscapeDataString(q)}"));

        var reference = (await GetAsync(id)).GetProperty("reference").GetString()!;
        Assert.Equal([id], await Found(reference));
        Assert.Equal([id], await Found(reference.ToLowerInvariant()));
        Assert.Equal([id], await Found($"Wheel came off the stand {_suffix}"));
        Assert.Equal([id], await Found($"Corridor {_suffix}"));
        Assert.Contains(id, await Found(_icuTag));
        Assert.Empty(await Found($"nothing-like-this-{_suffix}"));
    }

    [Fact]
    public async Task The_list_pages_and_says_what_each_incident_is()
    {
        for (var i = 0; i < 3; i++)
        {
            await ReportAsync(Incident(description: $"Paged one {i} {_suffix}", type: 40, damage: 20));
        }

        var page1 = await ListAsync($"q={_suffix}&pageSize=2");
        Assert.Equal(3, page1.GetProperty("total").GetInt32());
        Assert.Equal(2, page1.GetProperty("items").GetArrayLength());

        var row = page1.GetProperty("items")[0];
        Assert.Equal("Collision", row.GetProperty("typeLabel").GetString());
        Assert.Equal("Minor damage, still works", row.GetProperty("damageLabel").GetString());
        Assert.Equal(_icuTag, row.GetProperty("assetTag").GetString());
        Assert.Contains("Paged one", row.GetProperty("summary").GetString());

        Assert.Equal(1, (await ListAsync($"q={_suffix}&pageSize=2&page=2")).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Asking_for_all_is_the_same_as_not_narrowing_by_status()
    {
        var open = await ReportAsync(Incident(description: $"All one {_suffix}"));
        var closed = await ReportAsync(Incident(description: $"All two {_suffix}"));
        await _engineer.PostAsJsonAsync($"/api/incidents/{closed}/close", new { findings = "Done" });

        Assert.Equal(new[] { open, closed }.Order(), Ids(await ListAsync($"q={_suffix}&status=all")).Order());
        Assert.Equal(new[] { open, closed }.Order(), Ids(await ListAsync($"q={_suffix}")).Order());
    }

    [Fact]
    public async Task A_filter_that_means_nothing_is_refused()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _engineer.GetAsync("/api/incidents?status=lost")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _engineer.GetAsync("/api/incidents?type=7")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _engineer.GetAsync("/api/incidents/2000000000")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _engineer.GetAsync("/api/incidents/2000000000/report.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _engineer.PutAsJsonAsync("/api/incidents/2000000000/review", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _engineer.PostAsJsonAsync("/api/incidents/2000000000/close", new { })).StatusCode);
    }

    // ---------------------------------------------------------------- the report

    [Fact]
    public async Task The_summary_counts_by_kind_by_state_by_place_and_finds_the_machines_that_come_up_again()
    {
        // A period of a few days before today that nothing else in the shared database falls in.
        var day = Day(-400);
        await ReportAsync(Incident(machineId: _icuMachineId, type: 10, damage: 30, occurredOn: day, takenOutOfUse: true));
        await ReportAsync(Incident(machineId: _icuMachineId, type: 10, damage: 10, occurredOn: day));
        await ReportAsync(Incident(machineId: _icuSecondMachineId, type: 20, damage: 40, occurredOn: day));
        var closed = await ReportAsync(Incident(machineId: _icuSecondMachineId, type: 30, damage: 20, occurredOn: day));
        await _engineer.PostAsJsonAsync($"/api/incidents/{closed}/close", new { findings = "Spill from above" });

        var s = await (await _engineer.GetAsync($"/api/incidents/summary?from={day}&to={day}")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(4, s.GetProperty("total").GetInt32());
        Assert.Equal(3, s.GetProperty("open").GetInt32());
        Assert.Equal(1, s.GetProperty("closed").GetInt32());
        Assert.Equal(1, s.GetProperty("takenOutOfUse").GetInt32());

        var byType = s.GetProperty("byType").EnumerateArray().ToDictionary(c => c.GetProperty("label").GetString()!, c => c.GetProperty("value").GetInt32());
        Assert.Equal(2, byType["Fall or drop"]);
        Assert.Equal(1, byType["Mishandling"]);
        Assert.Equal(1, byType["Liquid damage"]);

        // All four states are listed, including the ones nothing was left in.
        var byDamage = s.GetProperty("byDamage").EnumerateArray().Select(c => c.GetProperty("value").GetInt32()).ToArray();
        Assert.Equal([1, 1, 1, 1], byDamage);

        var byPlace = s.GetProperty("byLocation").EnumerateArray().Single();
        Assert.Equal($"ICU bay {_suffix}", byPlace.GetProperty("label").GetString());
        Assert.Equal(4, byPlace.GetProperty("value").GetInt32());

        // Both machines had two each.
        var repeat = s.GetProperty("repeatMachines").EnumerateArray().ToList();
        Assert.Equal(2, repeat.Count);
        Assert.All(repeat, m => Assert.Equal(2, m.GetProperty("incidents").GetInt32()));
    }

    [Fact]
    public async Task The_summary_follows_the_period_asked_for()
    {
        var old = Day(-500);
        await ReportAsync(Incident(occurredOn: old));

        var inside = await (await _engineer.GetAsync($"/api/incidents/summary?from={old}&to={old}")).Content.ReadFromJsonAsync<JsonElement>();
        var outside = await (await _engineer.GetAsync($"/api/incidents/summary?from={Day(-499)}&to={Day(-498)}")).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(inside.GetProperty("total").GetInt32() >= 1);
        Assert.Equal(0, outside.GetProperty("total").GetInt32());
        Assert.Empty(outside.GetProperty("repeatMachines").EnumerateArray());
    }

    [Fact]
    public async Task The_printed_incident_report_is_a_pdf_with_what_was_found()
    {
        var id = await ReportAsync(Incident(damage: 30, occurredAt: "09:15", involved: "Technician, store"));
        await _engineer.PostAsJsonAsync($"/api/incidents/{id}/close", new { findings = "Wheel lock not engaged", correctiveAction = "Brake check added" });

        var reference = (await GetAsync(id)).GetProperty("reference").GetString();
        var res = await _engineer.GetAsync($"/api/incidents/{id}/report.pdf");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"{reference}.pdf", res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
    }

    [Fact]
    public async Task An_incident_that_is_not_reviewed_yet_still_prints()
    {
        var id = await ReportAsync(Incident());

        var res = await _engineer.GetAsync($"/api/incidents/{id}/report.pdf");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(await res.Content.ReadAsByteArrayAsync(), 0, 4));
    }

    [Fact]
    public async Task The_summary_report_prints_for_a_period_with_incidents_and_for_one_with_none()
    {
        var day = Day(-300);
        await ReportAsync(Incident(occurredOn: day));

        var some = await _engineer.GetAsync($"/api/incidents/report.pdf?from={day}&to={day}");
        Assert.Equal(HttpStatusCode.OK, some.StatusCode);
        Assert.Equal("application/pdf", some.Content.Headers.ContentType?.MediaType);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(await some.Content.ReadAsByteArrayAsync(), 0, 4));

        var none = await _engineer.GetAsync($"/api/incidents/report.pdf?from={Day(-2000)}&to={Day(-1990)}");
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);

        var all = await _engineer.GetAsync("/api/incidents/report.pdf");
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);

        // A ward prints its own.
        Assert.Equal(HttpStatusCode.OK, (await _ward.GetAsync("/api/incidents/report.pdf")).StatusCode);

        var backwards = await _engineer.GetAsync($"/api/incidents/report.pdf?from={Day(-1)}&to={Day(-5)}");
        Assert.Equal(HttpStatusCode.BadRequest, backwards.StatusCode);
    }

    // ---------------------------------------------------------------- the record

    [Fact]
    public async Task The_database_writes_the_audit_trail_and_it_says_who_reported_and_who_closed()
    {
        var id = await ReportAsync(Incident(), _ward);
        await _engineer.PostAsJsonAsync($"/api/incidents/{id}/close", new { findings = "Rough handling" });

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT operation, changed_by FROM audit_log WHERE table_name = 'incident' AND record_pk = @id ORDER BY id", conn);
        cmd.Parameters.AddWithValue("id", id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var rows = new List<(string Operation, string? By)>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }

        Assert.Equal(["INSERT", "UPDATE"], rows.Select(r => r.Operation).ToArray());
        Assert.Equal(_wardId.ToString(System.Globalization.CultureInfo.InvariantCulture), rows[0].By);
        Assert.Equal(_engineerId.ToString(System.Globalization.CultureInfo.InvariantCulture), rows[1].By);
    }
}
