using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Loss of equipment, in three steps: a department (or an engineer, or the head) writes up a machine that was lost, stolen or
/// lost while away; a biomedical engineer looks into it and approves it for the head; the head makes the final decision. While
/// it is open the register shows the machine as Lost. Seen only by the department, the engineers and the head.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class EquipmentLossTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "LossOfKit2026!";
    private const int StatusInStore = 10;
    private const int StatusInService = 20;
    private const int StatusLost = 60;

    private ApiFactory _factory = null!;
    private string _suffix = null!;
    private int _typeId;
    private int _icuDepartmentId;
    private int _icuRoomId;
    private int _otRoomId;

    private HttpClient _head = null!;
    private HttpClient _engineer = null!;
    private HttpClient _it = null!;
    private HttpClient _ward = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var type = new EquipmentType { Code = $"ls-{_suffix}", Name = $"Loss type {_suffix}" };
            var building = new Location { Code = $"LSB-{_suffix}", Name = $"Loss block {_suffix}", Level = LocationLevel.Building };
            db.EquipmentTypes.Add(type);
            db.Locations.Add(building);
            await db.SaveChangesAsync();
            _typeId = type.Id;

            var icu = new Location { Code = $"LSI-{_suffix}", Name = $"Loss ICU {_suffix}", Level = LocationLevel.Department, ParentId = building.Id };
            var ot = new Location { Code = $"LSO-{_suffix}", Name = $"Loss OT {_suffix}", Level = LocationLevel.Department, ParentId = building.Id };
            db.Locations.AddRange(icu, ot);
            await db.SaveChangesAsync();

            var icuRoom = new Location { Code = $"LSIR-{_suffix}", Name = $"Loss ICU bay {_suffix}", Level = LocationLevel.Room, ParentId = icu.Id };
            var otRoom = new Location { Code = $"LSOR-{_suffix}", Name = $"Loss OT room {_suffix}", Level = LocationLevel.Room, ParentId = ot.Id };
            db.Locations.AddRange(icuRoom, otRoom);
            await db.SaveChangesAsync();

            _icuDepartmentId = icu.Id;
            _icuRoomId = icuRoom.Id;
            _otRoomId = otRoom.Id;
        }

        _head = await SignInAsync("ls-head", Roles.BmeHead);
        _engineer = await SignInAsync("ls-eng", Roles.BmeEngineer);
        _it = await SignInAsync("ls-it", Roles.ItAdmin);
        int wardId;
        (_ward, wardId) = await SignInWithIdAsync("ls-ward", Roles.DepartmentUser);

        // The ward user belongs to the ICU, and so to everything beneath it.
        Assert.Equal(HttpStatusCode.NoContent,
            (await _head.PutAsJsonAsync($"/api/users/{wardId}/departments", new { locationIds = new[] { _icuDepartmentId } })).StatusCode);
    }

    private async Task<HttpClient> SignInAsync(string prefix, string role) => (await SignInWithIdAsync(prefix, role)).Client;

    private async Task<(HttpClient Client, int UserId)> SignInWithIdAsync(string prefix, string role)
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
        DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(330).AddDays(daysFromToday)).ToString("yyyy-MM-dd");

    private async Task<int> MachineAsync(string name, int? roomId = null, EquipmentStatus status = EquipmentStatus.InService, decimal? cost = null)
    {
        await using var db = fixture.CreateContext();
        var machine = new Domain.Assets.Equipment
        {
            AssetTag = $"LS{name}-{_suffix}".ToUpperInvariant(), EquipmentTypeId = _typeId, LocationId = roomId ?? _icuRoomId,
            Status = status, PurchaseCost = cost,
        };
        db.Equipment.Add(machine);
        await db.SaveChangesAsync();
        return machine.Id;
    }

    private async Task<int> StatusOfAsync(int machineId)
    {
        await using var db = fixture.CreateContext();
        return (int)await db.Equipment.IgnoreQueryFilters().Where(e => e.Id == machineId).Select(e => e.Status).SingleAsync();
    }

    private static object Loss(int machineId, int kind = 10, string? noticed = null, string? lastSeen = null,
        string? description = "Not on its stand at the morning round", decimal? value = null, string? fir = null, string? firOn = null) => new
    {
        equipmentId = machineId,
        kind,
        noticedOn = noticed,
        lastSeenOn = lastSeen,
        lastSeenPlace = "Recovery bay",
        description,
        lastHandledBy = "Staff nurse",
        policeReportNumber = fir,
        policeReportedOn = firOn,
        estimatedValue = value,
    };

    private async Task<int> ReportAsync(HttpClient who, object body)
    {
        var res = await who.PostAsJsonAsync("/api/losses", body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private static async Task<JsonElement> GetAsync(HttpClient who, int id) =>
        await who.GetFromJsonAsync<JsonElement>($"/api/losses/{id}");

    // ---------------------------------------------------------------- reporting

    [Fact]
    public async Task Writing_up_a_loss_marks_the_machine_lost_and_takes_its_cost_as_the_value()
    {
        var machine = await MachineAsync("A", cost: 250000m);

        var id = await ReportAsync(_engineer, Loss(machine, kind: 20, noticed: Day(0), lastSeen: Day(-2), fir: "FIR 118/2026", firOn: Day(0)));

        var loss = await GetAsync(_engineer, id);
        Assert.Matches(@"^LOSS-\d{4}-\d{5}$", loss.GetProperty("reference").GetString());
        // An engineer wrote it, so the engineer's step is theirs already: it waits for the head, approved by them.
        Assert.Equal("AwaitingDecision", loss.GetProperty("status").GetString());
        Assert.Equal("ls-eng person", loss.GetProperty("approvedByName").GetString());
        Assert.Equal("Stolen", loss.GetProperty("kindLabel").GetString());
        Assert.Equal(250000m, loss.GetProperty("estimatedValue").GetDecimal());
        Assert.Equal("FIR 118/2026", loss.GetProperty("policeReportNumber").GetString());
        Assert.Equal($"LS{"A"}-{_suffix}".ToUpperInvariant(), loss.GetProperty("machine").GetProperty("assetTag").GetString());
        Assert.Equal(StatusLost, await StatusOfAsync(machine));
    }

    [Fact]
    public async Task A_value_that_is_given_wins_over_the_register_cost()
    {
        var machine = await MachineAsync("B", cost: 90000m);
        var id = await ReportAsync(_head, Loss(machine, value: 12500.5m));
        Assert.Equal(12500.5m, (await GetAsync(_head, id)).GetProperty("estimatedValue").GetDecimal());
    }

    [Fact]
    public async Task A_loss_that_makes_no_sense_is_refused_with_the_reason()
    {
        var machine = await MachineAsync("C");

        async Task Refused(object body, string part)
        {
            var res = await _engineer.PostAsJsonAsync("/api/losses", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Contains(part, await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }

        await Refused(Loss(machine, kind: 99), "how the machine was lost");
        await Refused(Loss(machine, description: "  "), "describe");
        await Refused(Loss(machine, noticed: Day(3)), "has not come yet");
        await Refused(Loss(machine, noticed: Day(-1), lastSeen: Day(0)), "last seen after");
        await Refused(Loss(machine, value: -5m), "not below zero");
        await Refused(Loss(2000000000), "choose the machine");

        // Nothing was marked lost by any of those.
        Assert.Equal(StatusInService, await StatusOfAsync(machine));
    }

    [Fact]
    public async Task A_machine_cannot_be_lost_twice_or_after_it_is_disposed_of()
    {
        var machine = await MachineAsync("D");
        await ReportAsync(_engineer, Loss(machine));

        var again = await _engineer.PostAsJsonAsync("/api/losses", Loss(machine));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains("already recorded as lost", await again.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var gone = await MachineAsync("E", status: EquipmentStatus.Disposed);
        var res = await _engineer.PostAsJsonAsync("/api/losses", Loss(gone));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("disposed", await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- what Lost does to the register

    [Fact]
    public async Task A_lost_machine_cannot_be_given_a_status_by_hand_or_moved_or_sent_out()
    {
        var machine = await MachineAsync("F");
        await ReportAsync(_engineer, Loss(machine));

        var record = await _head.GetFromJsonAsync<JsonElement>($"/api/equipment/{machine}");
        string Edit(int status) => JsonSerializer.Serialize(new
        {
            assetTag = record.GetProperty("assetTag").GetString(),
            equipmentTypeId = _typeId,
            locationId = _icuRoomId,
            status,
        });

        var setInService = await _head.PutAsync($"/api/equipment/{machine}",
            new StringContent(Edit(StatusInService), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, setInService.StatusCode);
        Assert.Contains("loss record", await setInService.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // Saving the form with Lost as it stands is fine.
        var keep = await _head.PutAsync($"/api/equipment/{machine}",
            new StringContent(Edit(StatusLost), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NoContent, keep.StatusCode);

        // And nobody can mark a healthy machine Lost by hand.
        var healthy = await MachineAsync("G");
        var hand = await _head.PutAsync($"/api/equipment/{healthy}",
            new StringContent(JsonSerializer.Serialize(new
            {
                assetTag = $"LSG-{_suffix}".ToUpperInvariant(), equipmentTypeId = _typeId, locationId = _icuRoomId, status = StatusLost,
            }), System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, hand.StatusCode);
        Assert.Equal(StatusInService, await StatusOfAsync(healthy));

        var move = await _engineer.PostAsJsonAsync($"/api/equipment/{machine}/move", new { toLocationId = _otRoomId, reason = "Tidy up" });
        Assert.Equal(HttpStatusCode.Conflict, move.StatusCode);
        Assert.Contains("recorded as lost", await move.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var pass = await _engineer.PostAsJsonAsync("/api/gate-passes", new
        {
            vendorName = $"Vendor {_suffix}", items = new[] { new { equipmentId = machine, quantity = 1 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, pass.StatusCode);
        Assert.Contains("recorded as lost", await pass.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- how it ends

    [Fact]
    public async Task Looking_into_it_moves_it_on_and_finding_the_machine_puts_it_back_as_it_was()
    {
        // One that was in store comes back to store, not to a ward.
        var machine = await MachineAsync("H", status: EquipmentStatus.InStore);
        var id = await ReportAsync(_ward, Loss(machine));
        Assert.Equal(StatusLost, await StatusOfAsync(machine));

        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PutAsJsonAsync($"/api/losses/{id}/review", new
        {
            findings = "Checked every ward", correctiveAction = "Sign-out book", policeReportNumber = "FIR 9/2026", estimatedValue = 70000m,
        })).StatusCode);

        var reviewed = await GetAsync(_engineer, id);
        Assert.Equal("Investigating", reviewed.GetProperty("status").GetString());
        Assert.Equal("Checked every ward", reviewed.GetProperty("findings").GetString());
        Assert.Equal("FIR 9/2026", reviewed.GetProperty("policeReportNumber").GetString());
        Assert.Equal(70000m, reviewed.GetProperty("estimatedValue").GetDecimal());

        var found = await _engineer.PostAsJsonAsync($"/api/losses/{id}/recover", new { recoveredOn = Day(0) });
        Assert.Equal(HttpStatusCode.NoContent, found.StatusCode);

        var done = await GetAsync(_engineer, id);
        Assert.Equal("Recovered", done.GetProperty("status").GetString());
        Assert.True(done.GetProperty("isFinal").GetBoolean());
        Assert.Equal(Day(0), done.GetProperty("recoveredOn").GetString());
        Assert.Equal(StatusInStore, await StatusOfAsync(machine));
    }

    [Fact]
    public async Task The_day_it_was_found_must_be_a_possible_day()
    {
        var machine = await MachineAsync("I");
        var id = await ReportAsync(_engineer, Loss(machine, noticed: Day(-3)));

        Assert.Equal(HttpStatusCode.BadRequest, (await _engineer.PostAsJsonAsync($"/api/losses/{id}/recover", new { recoveredOn = Day(2) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _engineer.PostAsJsonAsync($"/api/losses/{id}/recover", new { recoveredOn = Day(-5) })).StatusCode);
        Assert.Equal(StatusLost, await StatusOfAsync(machine));
    }

    [Fact]
    public async Task A_write_off_is_the_heads_alone_needs_what_was_found_out_and_leaves_the_machine_lost_for_good()
    {
        var machine = await MachineAsync("J");
        var id = await ReportAsync(_engineer, Loss(machine));      // at the head's desk: an engineer wrote it

        // The engineer cannot make the final call.
        Assert.Equal(HttpStatusCode.Forbidden, (await _engineer.PostAsJsonAsync($"/api/losses/{id}/write-off", new { findings = "x" })).StatusCode);

        var bare = await _head.PostAsJsonAsync($"/api/losses/{id}/write-off", new { });
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Contains("found out", await bare.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _head.PostAsJsonAsync($"/api/losses/{id}/write-off", new { findings = "Not found after a full search", decisionNotes = "Insurance to be told" })).StatusCode);

        var closed = await GetAsync(_head, id);
        Assert.Equal("WrittenOff", closed.GetProperty("status").GetString());
        Assert.Equal("Insurance to be told", closed.GetProperty("decisionNotes").GetString());
        Assert.Equal(StatusLost, await StatusOfAsync(machine));

        // Final, for the head as well, by the API and by the database.
        Assert.Equal(HttpStatusCode.Conflict, (await _head.PutAsJsonAsync($"/api/losses/{id}/review", new { findings = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _head.PostAsJsonAsync($"/api/losses/{id}/recover", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _head.PostAsJsonAsync($"/api/losses/{id}/write-off", new { findings = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _head.PutAsJsonAsync($"/api/losses/{id}", Loss(machine))).StatusCode);

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE equipment_loss SET description = 'rewritten' WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState);
    }

    // ---------------------------------------------------------------- the three steps

    [Fact]
    public async Task A_departments_loss_waits_for_an_engineer_who_approves_it_for_the_head_who_decides()
    {
        var machine = await MachineAsync("S1");
        var id = await ReportAsync(_ward, Loss(machine));

        var first = await GetAsync(_engineer, id);
        Assert.Equal("Reported", first.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("approvedByName").ValueKind);

        // The head cannot write off what no engineer has looked at.
        var tooSoon = await _head.PostAsJsonAsync($"/api/losses/{id}/write-off", new { findings = "x" });
        Assert.Equal(HttpStatusCode.Conflict, tooSoon.StatusCode);
        Assert.Contains("not been approved", await tooSoon.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // The engineer has to say what they found out before it goes up.
        var bare = await _engineer.PostAsJsonAsync($"/api/losses/{id}/approve", new { });
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Contains("found out", await bare.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Reported", (await GetAsync(_engineer, id)).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/losses/{id}/approve", new
        {
            findings = "Searched every ward and the store", correctiveAction = "Sign-out book",
        })).StatusCode);

        var approved = await GetAsync(_head, id);
        Assert.Equal("AwaitingDecision", approved.GetProperty("status").GetString());
        Assert.Equal("ls-eng person", approved.GetProperty("approvedByName").GetString());
        Assert.NotEqual(JsonValueKind.Null, approved.GetProperty("approvedAtUtc").ValueKind);

        // Approving twice is refused, and so is looking into it again as if it had not been.
        Assert.Equal(HttpStatusCode.Conflict, (await _engineer.PostAsJsonAsync($"/api/losses/{id}/approve", new { findings = "again" })).StatusCode);

        // Saving a note while it waits for the head does not take it back a step.
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PutAsJsonAsync($"/api/losses/{id}/review", new { correctiveAction = "Also a CCTV check" })).StatusCode);
        Assert.Equal("AwaitingDecision", (await GetAsync(_head, id)).GetProperty("status").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await _head.PostAsJsonAsync($"/api/losses/{id}/write-off", new { })).StatusCode);
        Assert.Equal("WrittenOff", (await GetAsync(_head, id)).GetProperty("status").GetString());
        Assert.Equal(StatusLost, await StatusOfAsync(machine));
    }

    [Fact]
    public async Task The_head_can_send_a_loss_back_with_what_is_wanted_and_the_engineer_has_to_approve_it_again()
    {
        var machine = await MachineAsync("S2");
        var id = await ReportAsync(_ward, Loss(machine));
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/losses/{id}/approve", new { findings = "First look" })).StatusCode);

        // Not the engineer's call, and not without saying what is wanted.
        Assert.Equal(HttpStatusCode.Forbidden, (await _engineer.PostAsJsonAsync($"/api/losses/{id}/send-back", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _head.PostAsJsonAsync($"/api/losses/{id}/send-back", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _head.PostAsJsonAsync($"/api/losses/{id}/send-back", new { reason = "  " })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _head.PostAsJsonAsync($"/api/losses/{id}/send-back", new { reason = "Check the vendor's gate pass records" })).StatusCode);

        var back = await GetAsync(_engineer, id);
        Assert.Equal("Investigating", back.GetProperty("status").GetString());
        Assert.Equal("Check the vendor's gate pass records", back.GetProperty("decisionNotes").GetString());
        Assert.Equal(JsonValueKind.Null, back.GetProperty("approvedByName").ValueKind);   // it has to be approved again
        Assert.Equal(HttpStatusCode.Conflict, (await _head.PostAsJsonAsync($"/api/losses/{id}/write-off", new { findings = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _head.PostAsJsonAsync($"/api/losses/{id}/send-back", new { reason = "again" })).StatusCode);

        // Approved again, it is with the head again, and the old instruction has been answered.
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/losses/{id}/approve", new { findings = "Checked the gate passes: it never left" })).StatusCode);
        var again = await GetAsync(_head, id);
        Assert.Equal("AwaitingDecision", again.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("decisionNotes").ValueKind);
    }

    [Fact]
    public async Task The_head_can_write_up_review_and_decide_a_loss_alone()
    {
        var machine = await MachineAsync("S3");
        var id = await ReportAsync(_head, Loss(machine));
        Assert.Equal("AwaitingDecision", (await GetAsync(_head, id)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await _head.PostAsJsonAsync($"/api/losses/{id}/send-back", new { reason = "Look again" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _head.PostAsJsonAsync($"/api/losses/{id}/approve", new { findings = "Looked again" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _head.PostAsJsonAsync($"/api/losses/{id}/write-off", new { })).StatusCode);
    }

    [Fact]
    public async Task The_machine_can_be_found_at_any_step_by_an_engineer_or_the_head_and_a_loss_can_be_rejected_by_an_engineer()
    {
        var atEngineer = await MachineAsync("S4", status: EquipmentStatus.InStore);
        var atHead = await MachineAsync("S5");
        var rejected = await MachineAsync("S6");
        var one = await ReportAsync(_ward, Loss(atEngineer));
        var two = await ReportAsync(_engineer, Loss(atHead));
        var three = await ReportAsync(_ward, Loss(rejected));

        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/losses/{one}/recover", new { })).StatusCode);
        Assert.Equal(StatusInStore, await StatusOfAsync(atEngineer));

        Assert.Equal(HttpStatusCode.NoContent, (await _head.PostAsJsonAsync($"/api/losses/{two}/recover", new { decisionNotes = "Back in the OT store" })).StatusCode);
        Assert.Equal(StatusInService, await StatusOfAsync(atHead));
        Assert.Equal("Back in the OT store", (await GetAsync(_head, two)).GetProperty("decisionNotes").GetString());

        // An engineer rejects a department's report that was a mistake, with the reason.
        Assert.Equal(HttpStatusCode.NoContent,
            (await _engineer.PostAsJsonAsync($"/api/losses/{three}/cancel", new { findings = "It was in the ward store" })).StatusCode);
        Assert.Equal(StatusInService, await StatusOfAsync(rejected));
    }

    [Fact]
    public async Task Only_the_steps_that_are_theirs_are_open_to_each_role()
    {
        var machine = await MachineAsync("S7");
        var id = await ReportAsync(_ward, Loss(machine));
        var atHead = await ReportAsync(_engineer, Loss(await MachineAsync("S8")));

        // The department can write it up and read it, and that is all.
        foreach (var url in new[] { $"/api/losses/{id}/approve", $"/api/losses/{id}/recover", $"/api/losses/{id}/cancel", $"/api/losses/{atHead}/write-off", $"/api/losses/{atHead}/send-back" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await _ward.PostAsJsonAsync(url, new { findings = "x", reason = "x" })).StatusCode);
        }

        // The engineer approves, finds and rejects, and does not decide.
        Assert.Equal(HttpStatusCode.Forbidden, (await _engineer.PostAsJsonAsync($"/api/losses/{atHead}/write-off", new { findings = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _engineer.PostAsJsonAsync($"/api/losses/{atHead}/send-back", new { reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task The_list_has_a_desk_for_what_waits_for_an_engineer_and_what_waits_for_the_head()
    {
        var a = await MachineAsync("S9");
        var b = await MachineAsync("SA");
        await ReportAsync(_ward, Loss(a));
        await ReportAsync(_engineer, Loss(b));

        string[] Tags(JsonElement list) => list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("assetTag").GetString()!).ToArray();

        var engineer = await _head.GetFromJsonAsync<JsonElement>($"/api/losses?status=engineer&q={_suffix}");
        Assert.Equal([$"LSS9-{_suffix}".ToUpperInvariant()], Tags(engineer));

        var head = await _head.GetFromJsonAsync<JsonElement>($"/api/losses?status=awaiting&q={_suffix}");
        Assert.Equal([$"LSSA-{_suffix}".ToUpperInvariant()], Tags(head));

        var open = await _head.GetFromJsonAsync<JsonElement>($"/api/losses?status=open&q={_suffix}");
        Assert.Equal(2, open.GetProperty("total").GetInt32());

        var counts = open.GetProperty("counts");
        Assert.True(counts.GetProperty("withEngineer").GetInt32() >= 1);
        Assert.True(counts.GetProperty("awaitingDecision").GetInt32() >= 1);
        Assert.True(counts.GetProperty("open").GetInt32() >= 2);
    }

    [Fact]
    public async Task The_database_will_not_hold_a_loss_that_waits_for_the_head_or_is_written_off_without_an_approval()
    {
        var machine = await MachineAsync("SB");
        var id = await ReportAsync(_ward, Loss(machine));       // Reported: nobody has approved it

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE equipment_loss SET status = 25 WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task Cancelling_a_loss_written_by_mistake_needs_a_reason_and_puts_the_machine_back()
    {
        var machine = await MachineAsync("K");
        var id = await ReportAsync(_ward, Loss(machine));

        Assert.Equal(HttpStatusCode.BadRequest, (await _engineer.PostAsJsonAsync($"/api/losses/{id}/cancel", new { })).StatusCode);
        Assert.Equal(StatusLost, await StatusOfAsync(machine));

        Assert.Equal(HttpStatusCode.NoContent,
            (await _engineer.PostAsJsonAsync($"/api/losses/{id}/cancel", new { findings = "It was in the store all along" })).StatusCode);

        var cancelled = await GetAsync(_engineer, id);
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal("It was in the store all along", cancelled.GetProperty("findings").GetString());
        Assert.Equal(StatusInService, await StatusOfAsync(machine));

        // It can be written up again, now that it is not lost.
        await ReportAsync(_engineer, Loss(machine));
    }

    [Fact]
    public async Task A_loss_that_is_still_open_can_be_corrected_but_not_moved_to_another_machine()
    {
        var machine = await MachineAsync("L");
        var other = await MachineAsync("M");
        var id = await ReportAsync(_engineer, Loss(machine));

        Assert.Equal(HttpStatusCode.NoContent,
            (await _engineer.PutAsJsonAsync($"/api/losses/{id}", Loss(machine, kind: 30, description: "Lost on its way back from the vendor"))).StatusCode);
        var changed = await GetAsync(_engineer, id);
        Assert.Equal("Lost while away", changed.GetProperty("kindLabel").GetString());

        var wrong = await _engineer.PutAsJsonAsync($"/api/losses/{id}", Loss(other));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(StatusInService, await StatusOfAsync(other));
    }

    // ---------------------------------------------------------------- who can do what

    [Fact]
    public async Task A_ward_writes_up_its_own_departments_machines_but_cannot_look_into_them_or_see_the_rest()
    {
        var mine = await MachineAsync("N", _icuRoomId);
        var theirs = await MachineAsync("O", _otRoomId);

        var id = await ReportAsync(_ward, Loss(mine));
        Assert.Equal(HttpStatusCode.OK, (await _ward.GetAsync($"/api/losses/{id}")).StatusCode);

        // A machine in another department is simply unknown to this person.
        Assert.Equal(HttpStatusCode.BadRequest, (await _ward.PostAsJsonAsync("/api/losses", Loss(theirs))).StatusCode);
        Assert.Equal(StatusInService, await StatusOfAsync(theirs));

        var elsewhere = await ReportAsync(_engineer, Loss(theirs));
        Assert.Equal(HttpStatusCode.NotFound, (await _ward.GetAsync($"/api/losses/{elsewhere}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _ward.GetAsync($"/api/losses/{elsewhere}/report.pdf")).StatusCode);

        var list = await _ward.GetFromJsonAsync<JsonElement>($"/api/losses?status=all&q={_suffix}");
        var tags = list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("assetTag").GetString()).ToList();
        Assert.Contains($"LSN-{_suffix}".ToUpperInvariant(), tags);
        Assert.DoesNotContain($"LSO-{_suffix}".ToUpperInvariant(), tags);

        // Looking into it is the biomedical team's.
        Assert.Equal(HttpStatusCode.Forbidden, (await _ward.PutAsJsonAsync($"/api/losses/{id}", Loss(mine))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _ward.PutAsJsonAsync($"/api/losses/{id}/review", new { findings = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _ward.PostAsJsonAsync($"/api/losses/{id}/recover", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _ward.PostAsJsonAsync($"/api/losses/{id}/write-off", new { findings = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _ward.PostAsJsonAsync($"/api/losses/{id}/cancel", new { findings = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _ward.PostAsJsonAsync($"/api/losses/{id}/approve", new { findings = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _ward.PostAsJsonAsync($"/api/losses/{id}/send-back", new { reason = "x" })).StatusCode);
    }

    [Fact]
    public async Task The_it_team_cannot_see_losses_and_nobody_can_without_signing_in()
    {
        var machine = await MachineAsync("P");
        var id = await ReportAsync(_engineer, Loss(machine));

        Assert.Equal(HttpStatusCode.Forbidden, (await _it.GetAsync("/api/losses")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _it.GetAsync($"/api/losses/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _it.GetAsync("/api/losses/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _it.GetAsync("/api/losses/report.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _it.PostAsJsonAsync("/api/losses", Loss(machine))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _it.PostAsJsonAsync($"/api/losses/{id}/approve", new { findings = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _it.PostAsJsonAsync($"/api/losses/{id}/write-off", new { findings = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _it.GetAsync($"/api/reports/losses")).StatusCode);

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/losses")).StatusCode);
    }

    // ---------------------------------------------------------------- lists and reports

    [Fact]
    public async Task The_list_filters_by_status_kind_and_search_and_counts_each_tab()
    {
        var stolen = await MachineAsync("Q");
        var lost = await MachineAsync("R");
        var found = await MachineAsync("S");
        await ReportAsync(_engineer, Loss(stolen, kind: 20, fir: $"FIR-{_suffix}"));
        await ReportAsync(_engineer, Loss(lost, kind: 10));
        var foundId = await ReportAsync(_engineer, Loss(found, kind: 10));
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/losses/{foundId}/recover", new { })).StatusCode);

        var open = await _engineer.GetFromJsonAsync<JsonElement>($"/api/losses?status=open&q=LS&pageSize=200");
        var openTags = open.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("assetTag").GetString()).ToList();
        Assert.Contains($"LSQ-{_suffix}".ToUpperInvariant(), openTags);
        Assert.DoesNotContain($"LSS-{_suffix}".ToUpperInvariant(), openTags);

        var recovered = await _engineer.GetFromJsonAsync<JsonElement>($"/api/losses?status=recovered&q={_suffix}");
        Assert.Equal(1, recovered.GetProperty("total").GetInt32());
        Assert.True(recovered.GetProperty("counts").GetProperty("open").GetInt32() >= 2);

        var byKind = await _engineer.GetFromJsonAsync<JsonElement>($"/api/losses?status=all&kind=20&q={_suffix}");
        Assert.Equal(1, byKind.GetProperty("total").GetInt32());

        // A police complaint number is something to look it up by.
        var byFir = await _engineer.GetFromJsonAsync<JsonElement>($"/api/losses?status=all&q=FIR-{_suffix}");
        Assert.Equal(1, byFir.GetProperty("total").GetInt32());
        Assert.True(byFir.GetProperty("items")[0].GetProperty("hasPoliceReport").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await _engineer.GetAsync("/api/losses?status=sideways")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _engineer.GetAsync("/api/losses?kind=77")).StatusCode);
    }

    [Fact]
    public async Task The_summary_counts_what_was_lost_what_it_was_worth_and_leaves_out_cancelled_records()
    {
        var day = Day(-400);   // a day nobody else's test uses, so the period holds only this test's records
        var a = await MachineAsync("T", cost: 100000m);
        var b = await MachineAsync("U", cost: 50000m);
        var c = await MachineAsync("V");
        var mistake = await MachineAsync("W", cost: 999999m);

        await ReportAsync(_engineer, Loss(a, kind: 20, noticed: day));
        var bId = await ReportAsync(_engineer, Loss(b, kind: 10, noticed: day));
        await ReportAsync(_engineer, Loss(c, kind: 30, noticed: day));
        var mistakeId = await ReportAsync(_engineer, Loss(mistake, noticed: day));

        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/losses/{bId}/recover", new { recoveredOn = Day(-399) })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/losses/{mistakeId}/cancel", new { findings = "Wrong machine" })).StatusCode);

        var s = await _engineer.GetFromJsonAsync<JsonElement>($"/api/losses/summary?from={day}&to={day}");
        Assert.Equal(3, s.GetProperty("total").GetInt32());
        Assert.Equal(2, s.GetProperty("open").GetInt32());
        Assert.Equal(1, s.GetProperty("recovered").GetInt32());
        Assert.Equal(0, s.GetProperty("writtenOff").GetInt32());
        Assert.Equal(150000m, s.GetProperty("valueReported").GetDecimal());   // the cancelled one is not a loss
        Assert.Equal(100000m, s.GetProperty("valueLost").GetDecimal());
        Assert.Equal(50000m, s.GetProperty("valueRecovered").GetDecimal());
        Assert.Equal(1, s.GetProperty("withoutValue").GetInt32());

        var kinds = s.GetProperty("byKind").EnumerateArray().ToDictionary(k => k.GetProperty("label").GetString()!, k => k.GetProperty("value").GetInt32());
        Assert.Equal(1, kinds["Lost"]);
        Assert.Equal(1, kinds["Stolen"]);
        Assert.Equal(1, kinds["Lost while away"]);
    }

    [Fact]
    public async Task The_records_and_the_period_report_print_as_pdfs()
    {
        var machine = await MachineAsync("X", cost: 40000m);
        var id = await ReportAsync(_engineer, Loss(machine, fir: "FIR 5/2026", firOn: Day(0)));
        Assert.Equal(HttpStatusCode.NoContent, (await _head.PostAsJsonAsync($"/api/losses/{id}/write-off", new
        {
            findings = "Searched every ward", correctiveAction = "Machines are signed in and out", decisionNotes = "Insurer to be told",
        })).StatusCode);

        foreach (var url in new[] { $"/api/losses/{id}/report.pdf", "/api/losses/report.pdf", $"/api/losses/report.pdf?from={Day(-30)}&to={Day(0)}" })
        {
            var res = await _head.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
            var bytes = await res.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.Length > 1000);
            Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await _head.GetAsync($"/api/losses/report.pdf?from={Day(0)}&to={Day(-5)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _head.GetAsync("/api/losses/2000000000/report.pdf")).StatusCode);
    }

    // ---------------------------------------------------------------- the fleet report

    [Fact]
    public async Task The_reports_page_reads_losses_by_period_and_place_and_downloads_them_as_a_spreadsheet()
    {
        var day = Day(-500);   // a day nobody else's test uses
        var inIcu = await MachineAsync("Z1", _icuRoomId, cost: 30000m);
        var inOt = await MachineAsync("Z2", _otRoomId, cost: 70000m);
        var mistake = await MachineAsync("Z3", _icuRoomId, cost: 999999m);

        await ReportAsync(_engineer, Loss(inIcu, kind: 20, noticed: day, fir: "FIR Z/2026"));
        var foundId = await ReportAsync(_engineer, Loss(inOt, noticed: day));
        var mistakeId = await ReportAsync(_engineer, Loss(mistake, noticed: day));
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/losses/{foundId}/recover", new { recoveredOn = Day(-499) })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/losses/{mistakeId}/cancel", new { findings = "Wrong machine" })).StatusCode);

        var whole = await _head.GetFromJsonAsync<JsonElement>($"/api/reports/losses?from={day}&to={day}");
        Assert.Equal(2, whole.GetProperty("total").GetInt32());              // the cancelled one is not a loss
        Assert.Equal(1, whole.GetProperty("open").GetInt32());
        Assert.Equal(1, whole.GetProperty("recovered").GetInt32());
        Assert.Equal(1, whole.GetProperty("withPoliceComplaint").GetInt32());
        Assert.Equal(100000m, whole.GetProperty("valueReported").GetDecimal());
        Assert.Equal(30000m, whole.GetProperty("valueLost").GetDecimal());
        Assert.Equal(70000m, whole.GetProperty("valueRecovered").GetDecimal());
        Assert.Equal("Whole hospital", whole.GetProperty("scope").GetString());

        var recoveredRow = whole.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("status").GetString() == "Recovered");
        Assert.Equal(1, recoveredRow.GetProperty("daysMissing").GetInt32());

        // Narrowed to one department it holds only that department's machine.
        var icu = await _head.GetFromJsonAsync<JsonElement>($"/api/reports/losses?from={day}&to={day}&locationId={_icuDepartmentId}");
        Assert.Equal(1, icu.GetProperty("total").GetInt32());
        Assert.Equal($"LSZ1-{_suffix}".ToUpperInvariant(), icu.GetProperty("items")[0].GetProperty("assetTag").GetString());

        var csv = await _head.GetAsync($"/api/reports/losses/report.csv?from={day}&to={day}");
        Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
        Assert.StartsWith("text/csv", csv.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains("Reference,Machine number", text, StringComparison.Ordinal);
        Assert.Contains($"LSZ1-{_suffix}".ToUpperInvariant(), text, StringComparison.Ordinal);
        Assert.DoesNotContain($"LSZ3-{_suffix}".ToUpperInvariant(), text, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.BadRequest, (await _head.GetAsync($"/api/reports/losses?from={Day(0)}&to={Day(-3)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _head.GetAsync("/api/reports/losses?locationId=2000000000")).StatusCode);
    }

    [Fact]
    public async Task The_loss_report_is_for_those_who_may_see_reports_only()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _engineer.GetAsync("/api/reports/losses")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _engineer.GetAsync("/api/reports/losses/report.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _ward.GetAsync("/api/reports/losses")).StatusCode);

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/reports/losses")).StatusCode);
    }

    [Fact]
    public async Task The_database_keeps_one_open_loss_per_machine_and_the_kinds_in_range()
    {
        var machine = await MachineAsync("Y");
        var id = await ReportAsync(_engineer, Loss(machine));

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();

        // A second open loss for the same machine, written around the API, is refused.
        await using (var dup = new NpgsqlCommand(
            @"INSERT INTO equipment_loss (equipment_id, location_id, loss_kind, noticed_on, description, status, reported_by_user_id, reported_at_utc)
              SELECT equipment_id, location_id, loss_kind, noticed_on, 'again', 10, reported_by_user_id, now() FROM equipment_loss WHERE id = @id", conn))
        {
            dup.Parameters.AddWithValue("id", id);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => dup.ExecuteNonQueryAsync());
            Assert.Equal("23505", ex.SqlState);
        }

        await using var bad = new NpgsqlCommand("UPDATE equipment_loss SET loss_kind = 99 WHERE id = @id", conn);
        bad.Parameters.AddWithValue("id", id);
        var kind = await Assert.ThrowsAsync<PostgresException>(() => bad.ExecuteNonQueryAsync());
        Assert.Equal("23514", kind.SqlState);
    }
}
