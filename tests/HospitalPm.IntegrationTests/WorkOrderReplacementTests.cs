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

namespace HospitalPm.IntegrationTests;

/// <summary>
/// A repair takes time, so the engineer puts a free machine from the store in place of the faulty one and takes it back
/// afterwards. Only a machine that is really free is offered, the same kind first, and two requests cannot claim the same one.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class WorkOrderReplacementTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "StandIn2026!";
    private const int InProgress = 30;
    private const int Resolved = 50;

    private ApiFactory _factory = null!;
    private string _suffix = null!;
    private int _typeId;
    private int _otherTypeId;
    private int _wardId;
    private int _roomId;
    private int _storeId;
    private HttpClient _ward = null!;
    private HttpClient _engineer = null!;
    private HttpClient _head = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var type = new EquipmentType { Code = $"si-{_suffix}", Name = $"Stand type {_suffix}" };
            var other = new EquipmentType { Code = $"so-{_suffix}", Name = $"Other type {_suffix}" };
            var building = new Location { Code = $"SIB-{_suffix}", Name = $"Stand block {_suffix}", Level = LocationLevel.Building };
            db.EquipmentTypes.AddRange(type, other);
            db.Locations.Add(building);
            await db.SaveChangesAsync();

            var ward = new Location { Code = $"SID-{_suffix}", Name = $"Stand ward {_suffix}", Level = LocationLevel.Department, ParentId = building.Id };
            var store = new Location { Code = $"SIS-{_suffix}", Name = $"Stand store {_suffix}", Level = LocationLevel.Department, ParentId = building.Id };
            db.Locations.AddRange(ward, store);
            await db.SaveChangesAsync();

            var room = new Location { Code = $"SIR-{_suffix}", Name = $"Stand room {_suffix}", Level = LocationLevel.Room, ParentId = ward.Id };
            db.Locations.Add(room);
            await db.SaveChangesAsync();

            _typeId = type.Id;
            _otherTypeId = other.Id;
            _wardId = ward.Id;
            _roomId = room.Id;
            _storeId = store.Id;
        }

        _head = (await SignInAsync("si-head", Roles.BmeHead)).Client;
        _engineer = (await SignInAsync("si-eng", Roles.BmeEngineer)).Client;
        int wardUserId;
        (_ward, wardUserId) = await SignInAsync("si-ward", Roles.DepartmentUser);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _head.PutAsJsonAsync($"/api/users/{wardUserId}/departments", new { locationIds = new[] { _wardId } })).StatusCode);
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
        _ward?.Dispose();
        _engineer?.Dispose();
        _head?.Dispose();
        _factory?.Dispose();
    }

    private string Tag(string name) => $"SI{name}-{_suffix}".ToUpperInvariant();

    private async Task<int> MachineAsync(
        string name, EquipmentStatus status = EquipmentStatus.InStore, int? typeId = null, int? locationId = null,
        string? make = "Philips", string? model = "Efficia")
    {
        await using var db = fixture.CreateContext();
        var machine = new Domain.Assets.Equipment
        {
            AssetTag = Tag(name), EquipmentTypeId = typeId ?? _typeId, LocationId = locationId ?? _storeId, Status = status,
            Manufacturer = make, Model = model,
        };
        db.Equipment.Add(machine);
        await db.SaveChangesAsync();
        return machine.Id;
    }

    /// <summary>The faulty machine in the ward, with a service request placed by the ward on it.</summary>
    private async Task<(int Machine, int Order)> FaultyAsync(string name)
    {
        var machine = await MachineAsync(name, EquipmentStatus.InService, locationId: _roomId);
        return (machine, await RequestAsync(machine));
    }

    private async Task<int> RequestAsync(int machineId)
    {
        var res = await _ward.PostAsJsonAsync("/api/work-orders", new { equipmentId = machineId, faultDescription = "Will not switch on", priority = 20 });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private Task<HttpResponseMessage> ProvideAsync(HttpClient who, int order, int machine, string? note = null) =>
        who.PostAsJsonAsync($"/api/work-orders/{order}/replacement", new { equipmentId = machine, note });

    private Task<HttpResponseMessage> TakeBackAsync(HttpClient who, int order, string? note = null) =>
        who.PostAsJsonAsync($"/api/work-orders/{order}/replacement/return", new { note });

    private static async Task<string[]> OptionTagsAsync(HttpClient who, int order, string query = "")
    {
        var res = await who.GetFromJsonAsync<JsonElement>($"/api/work-orders/{order}/replacement-options{query}");
        return res.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("assetTag").GetString()!).ToArray();
    }

    private async Task<Domain.Assets.Equipment> ReadAsync(int machineId)
    {
        await using var db = fixture.CreateContext();
        return await db.Equipment.AsNoTracking().SingleAsync(e => e.Id == machineId);
    }

    private async Task<JsonElement> DetailAsync(HttpClient who, int order)
        => await who.GetFromJsonAsync<JsonElement>($"/api/work-orders/{order}");

    private async Task ResolveAsync(int order)
    {
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/work-orders/{order}/status", new { status = InProgress })).StatusCode);
        var done = await _engineer.PostAsJsonAsync($"/api/work-orders/{order}/resolve", new { resolutionNotes = "Power supply replaced" });
        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Only_a_free_machine_in_store_is_offered_and_the_same_kind_comes_first()
    {
        var (faulty, order) = await FaultyAsync("F1");

        _ = await MachineAsync("SAME");                                                   // same kind, same make and model
        _ = await MachineAsync("KIND", model: "Other model");                             // same kind, another model
        _ = await MachineAsync("DIFF", typeId: _otherTypeId);                             // another kind of machine
        _ = await MachineAsync("USE", EquipmentStatus.InService);                         // in use
        _ = await MachineAsync("FIX", EquipmentStatus.UnderRepair);                       // being repaired
        _ = await MachineAsync("LOST", EquipmentStatus.Lost);                             // lost
        _ = await MachineAsync("GONE", EquipmentStatus.Condemned);                        // retired
        var onPass = await MachineAsync("PASS");                                          // out of the hospital
        Assert.Equal(HttpStatusCode.Created, (await _engineer.PostAsJsonAsync("/api/gate-passes", new
        {
            vendorName = $"Vendor {_suffix}", items = new[] { new { equipmentId = onPass, quantity = 1 } },
        })).StatusCode);

        var offered = await OptionTagsAsync(_engineer, order, $"?q={_suffix}");
        Assert.Equal([Tag("SAME"), Tag("KIND")], offered);

        // The faulty machine itself is never offered, and other kinds only when they are asked for.
        Assert.DoesNotContain(Tag("F1"), offered);
        _ = faulty;
        var wider = await OptionTagsAsync(_engineer, order, $"?q={_suffix}&otherTypes=true");
        Assert.Equal([Tag("SAME"), Tag("KIND"), Tag("DIFF")], wider);

        // A search narrows it by number, make or model.
        Assert.Equal([Tag("KIND")], await OptionTagsAsync(_engineer, order, $"?q={Tag("KIND")}"));
        Assert.Equal([Tag("KIND")], await OptionTagsAsync(_engineer, order, "?q=other%20model"));

        // It says what it is looking for, so the page can say so.
        var all = await _engineer.GetFromJsonAsync<JsonElement>($"/api/work-orders/{order}/replacement-options?q={_suffix}");
        Assert.Equal(Tag("F1"), all.GetProperty("forMachine").GetProperty("assetTag").GetString());
        Assert.True(all.GetProperty("items")[0].GetProperty("sameModel").GetBoolean());
        Assert.False(all.GetProperty("items")[1].GetProperty("sameModel").GetBoolean());
    }

    [Fact]
    public async Task A_department_cannot_choose_or_take_back_a_replacement_but_can_see_which_one_stands_in()
    {
        var (_, order) = await FaultyAsync("F2");
        var spare = await MachineAsync("S2");

        Assert.Equal(HttpStatusCode.Forbidden, (await _ward.GetAsync($"/api/work-orders/{order}/replacement-options")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ProvideAsync(_ward, order, spare)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await TakeBackAsync(_ward, order)).StatusCode);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/work-orders/{order}/replacement-options")).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await ProvideAsync(_engineer, order, spare, "Checked this week")).StatusCode);

        // The ward sees which machine is standing in for theirs, where it came from, and the note, but is offered nothing to do.
        var seen = await DetailAsync(_ward, order);
        var row = seen.GetProperty("replacements")[0];
        Assert.Equal(Tag("S2"), row.GetProperty("assetTag").GetString());
        Assert.True(row.GetProperty("isActive").GetBoolean());
        Assert.Equal($"Stand store {_suffix}", row.GetProperty("fromLocationName").GetString());
        Assert.Equal($"Stand room {_suffix}", row.GetProperty("placedAt").GetString());
        Assert.Equal("Checked this week", row.GetProperty("note").GetString());
        Assert.False(seen.GetProperty("canProvideReplacement").GetBoolean());
        Assert.False(seen.GetProperty("canTakeBackReplacement").GetBoolean());

        // Even after it has gone back to store, where the ward can no longer see the machine.
        Assert.Equal(HttpStatusCode.NoContent, (await TakeBackAsync(_engineer, order)).StatusCode);
        var after = (await DetailAsync(_ward, order)).GetProperty("replacements")[0];
        Assert.False(after.GetProperty("isActive").GetBoolean());
        Assert.Equal(Tag("S2"), after.GetProperty("assetTag").GetString());
    }

    [Fact]
    public async Task Providing_a_replacement_moves_it_to_the_faulty_machine_marks_it_in_use_and_notes_the_request()
    {
        var (faulty, order) = await FaultyAsync("F3");
        var spare = await MachineAsync("S3");

        var before = await DetailAsync(_engineer, order);
        Assert.True(before.GetProperty("canProvideReplacement").GetBoolean());
        Assert.False(before.GetProperty("canTakeBackReplacement").GetBoolean());

        var provided = await ProvideAsync(_head, order, spare);
        Assert.Equal(HttpStatusCode.Created, provided.StatusCode);

        var moved = await ReadAsync(spare);
        Assert.Equal(_roomId, moved.LocationId);
        Assert.Equal(EquipmentStatus.InService, moved.Status);

        // The move is on the machine's own record, with the reason in words.
        var moves = await _engineer.GetFromJsonAsync<JsonElement>($"/api/equipment/{spare}/moves");
        Assert.Equal(1, moves.GetArrayLength());
        Assert.Contains("Replacement for " + Tag("F3"), moves[0].GetProperty("reason").GetString(), StringComparison.Ordinal);

        // The request says so in its notes, and offers taking it back and nothing more.
        var detail = await DetailAsync(_engineer, order);
        Assert.Contains(detail.GetProperty("notes").EnumerateArray(),
            n => n.GetProperty("body").GetString()!.Contains("Replacement machine provided: " + Tag("S3"), StringComparison.Ordinal));
        Assert.False(detail.GetProperty("canProvideReplacement").GetBoolean());
        Assert.True(detail.GetProperty("canTakeBackReplacement").GetBoolean());

        // And the faulty machine is left alone.
        Assert.Equal(_roomId, (await ReadAsync(faulty)).LocationId);

        // It no longer appears in the list of free machines.
        var other = await RequestAsync(await MachineAsync("F3B", EquipmentStatus.InService, locationId: _roomId));
        Assert.DoesNotContain(Tag("S3"), await OptionTagsAsync(_engineer, other, $"?q={_suffix}"));
    }

    [Fact]
    public async Task One_stand_in_at_a_time_per_request_and_one_request_at_a_time_per_machine()
    {
        var (_, first) = await FaultyAsync("F4");
        var (_, second) = await FaultyAsync("F5");
        var spare = await MachineAsync("S4");
        var spare2 = await MachineAsync("S5");

        Assert.Equal(HttpStatusCode.Created, (await ProvideAsync(_engineer, first, spare)).StatusCode);

        // A second one for the same request says which is already there.
        var again = await ProvideAsync(_engineer, first, spare2);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains(Tag("S4"), await again.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // The same machine for another request: it is no longer in store, and the message says so.
        var taken = await ProvideAsync(_engineer, second, spare);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Contains("not free any more", await taken.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_machine_that_is_not_free_or_not_there_is_refused_with_the_reason()
    {
        var (faulty, order) = await FaultyAsync("F6");

        Assert.Equal(HttpStatusCode.BadRequest, (await ProvideAsync(_engineer, order, faulty)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ProvideAsync(_engineer, order, 2000000000)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ProvideAsync(_engineer, 2000000000, faulty)).StatusCode);

        var inUse = await MachineAsync("U6", EquipmentStatus.InService);
        Assert.Equal(HttpStatusCode.Conflict, (await ProvideAsync(_engineer, order, inUse)).StatusCode);

        var lost = await MachineAsync("L6", EquipmentStatus.Lost);
        Assert.Equal(HttpStatusCode.Conflict, (await ProvideAsync(_engineer, order, lost)).StatusCode);

        var outOnAPass = await MachineAsync("P6");
        Assert.Equal(HttpStatusCode.Created, (await _engineer.PostAsJsonAsync("/api/gate-passes", new
        {
            vendorName = $"Vendor {_suffix}", items = new[] { new { equipmentId = outOnAPass, quantity = 1 } },
        })).StatusCode);
        var passRefused = await ProvideAsync(_engineer, order, outOnAPass);
        Assert.Equal(HttpStatusCode.Conflict, passRefused.StatusCode);
        Assert.Contains("gate pass", await passRefused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var longNote = new string('x', 501);
        Assert.Equal(HttpStatusCode.BadRequest, (await ProvideAsync(_engineer, order, await MachineAsync("N6"), longNote)).StatusCode);

        // Nothing moved for any of them.
        Assert.Equal(_roomId, (await ReadAsync(faulty)).LocationId);
        Assert.Empty((await DetailAsync(_engineer, order)).GetProperty("replacements").EnumerateArray());
    }

    [Fact]
    public async Task Two_people_choosing_the_same_machine_at_once_get_one_yes_and_one_no()
    {
        var (_, first) = await FaultyAsync("F7");
        var (_, second) = await FaultyAsync("F8");
        var spare = await MachineAsync("S7");

        var results = await Task.WhenAll(ProvideAsync(_engineer, first, spare), ProvideAsync(_head, second, spare));
        var codes = results.Select(r => r.StatusCode).Order().ToArray();
        Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], codes);

        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.WorkOrderReplacements.CountAsync(r => r.ReplacementEquipmentId == spare && r.ReturnedAtUtc == null));
    }

    [Fact]
    public async Task Taking_it_back_puts_it_where_it_came_from_as_it_was_and_the_machine_can_be_used_again()
    {
        var (_, order) = await FaultyAsync("F9");
        var spare = await MachineAsync("S9");

        Assert.Equal(HttpStatusCode.Conflict, (await TakeBackAsync(_engineer, order)).StatusCode);   // nothing to take back yet

        Assert.Equal(HttpStatusCode.Created, (await ProvideAsync(_engineer, order, spare)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await TakeBackAsync(_engineer, order, "Cleaned first")).StatusCode);

        var back = await ReadAsync(spare);
        Assert.Equal(_storeId, back.LocationId);
        Assert.Equal(EquipmentStatus.InStore, back.Status);

        var moves = await _engineer.GetFromJsonAsync<JsonElement>($"/api/equipment/{spare}/moves");
        Assert.Equal(2, moves.GetArrayLength());

        var detail = await DetailAsync(_engineer, order);
        Assert.Contains(detail.GetProperty("notes").EnumerateArray(),
            n => n.GetProperty("body").GetString()!.Contains("taken back", StringComparison.Ordinal)
                 && n.GetProperty("body").GetString()!.Contains("Cleaned first", StringComparison.Ordinal));
        Assert.True(detail.GetProperty("canProvideReplacement").GetBoolean());
        Assert.False(detail.GetProperty("canTakeBackReplacement").GetBoolean());

        // It is free again, for this request or another, and the history keeps both.
        Assert.Contains(Tag("S9"), await OptionTagsAsync(_engineer, order, $"?q={_suffix}"));
        Assert.Equal(HttpStatusCode.Created, (await ProvideAsync(_engineer, order, spare)).StatusCode);
        Assert.Equal(2, (await DetailAsync(_engineer, order)).GetProperty("replacements").GetArrayLength());

        // Taking it back twice is refused the second time.
        Assert.Equal(HttpStatusCode.NoContent, (await TakeBackAsync(_engineer, order)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await TakeBackAsync(_engineer, order)).StatusCode);
    }

    [Fact]
    public async Task A_resolved_request_takes_no_new_replacement_but_the_old_one_can_still_be_taken_back()
    {
        var (_, order) = await FaultyAsync("FA");
        var spare = await MachineAsync("SA");
        var another = await MachineAsync("SB");

        Assert.Equal(HttpStatusCode.Created, (await ProvideAsync(_engineer, order, spare)).StatusCode);
        await ResolveAsync(order);

        var locked = await DetailAsync(_engineer, order);
        Assert.Equal(Resolved, locked.GetProperty("status").GetInt32());
        Assert.False(locked.GetProperty("canProvideReplacement").GetBoolean());
        Assert.True(locked.GetProperty("canTakeBackReplacement").GetBoolean());

        // Nothing more can be put in place on a locked request ...
        Assert.Equal(HttpStatusCode.NoContent, (await TakeBackAsync(_engineer, order)).StatusCode);
        var refused = await ProvideAsync(_engineer, order, another);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("locked", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // ... but the one that was is back where it came from, and the request's notes were left alone.
        var back = await ReadAsync(spare);
        Assert.Equal(_storeId, back.LocationId);
        Assert.Equal(EquipmentStatus.InStore, back.Status);
        Assert.DoesNotContain((await DetailAsync(_engineer, order)).GetProperty("notes").EnumerateArray(),
            n => n.GetProperty("body").GetString()!.Contains("was taken back", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_repair_that_has_sent_the_faulty_machine_out_on_a_gate_pass_can_still_have_a_stand_in()
    {
        var (faulty, order) = await FaultyAsync("FB");
        var spare = await MachineAsync("SC");

        Assert.Equal(HttpStatusCode.Created, (await _engineer.PostAsJsonAsync("/api/gate-passes", new
        {
            vendorName = $"Vendor {_suffix}", items = new[] { new { equipmentId = faulty, quantity = 1 } },
        })).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await ProvideAsync(_engineer, order, spare)).StatusCode);
        Assert.Equal(_roomId, (await ReadAsync(spare)).LocationId);
    }

    [Fact]
    public async Task A_machine_standing_in_for_another_cannot_be_sent_out_on_a_gate_pass()
    {
        var (_, order) = await FaultyAsync("FE");
        var spare = await MachineAsync("SF");
        Assert.Equal(HttpStatusCode.Created, (await ProvideAsync(_engineer, order, spare)).StatusCode);

        foreach (var returnable in new[] { true, false })
        {
            var res = await _engineer.PostAsJsonAsync("/api/gate-passes", new
            {
                vendorName = $"Vendor {_suffix}", isReturnable = returnable, items = new[] { new { equipmentId = spare, quantity = 1 } },
            });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Contains("standing in", await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }

        // Once it is taken back it can go.
        Assert.Equal(HttpStatusCode.NoContent, (await TakeBackAsync(_engineer, order)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await _engineer.PostAsJsonAsync("/api/gate-passes", new
        {
            vendorName = $"Vendor {_suffix}", items = new[] { new { equipmentId = spare, quantity = 1 } },
        })).StatusCode);
    }

    [Fact]
    public async Task A_machine_that_is_lost_or_retired_has_nothing_to_replace()
    {
        var (faulty, order) = await FaultyAsync("FC");
        var spare = await MachineAsync("SD");

        await using (var db = fixture.CreateContext())
        {
            await db.Equipment.Where(e => e.Id == faulty).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EquipmentStatus.Condemned));
        }

        var refused = await ProvideAsync(_engineer, order, spare);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("condemned", await refused.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.False((await DetailAsync(_engineer, order)).GetProperty("canProvideReplacement").GetBoolean());
    }

    [Fact]
    public async Task A_replacement_record_is_kept_for_good_and_a_finished_one_cannot_be_rewritten()
    {
        var (_, order) = await FaultyAsync("FD");
        var spare = await MachineAsync("SE");
        Assert.Equal(HttpStatusCode.Created, (await ProvideAsync(_engineer, order, spare)).StatusCode);

        await using var db = fixture.CreateContext();

        // While it is in place only the taking back can be written, not who or what or where.
        var change = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE work_order_replacement SET to_location_id = from_location_id WHERE work_order_id = {0}", order));
        Assert.Contains("Only the taking back", change.ToString(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await TakeBackAsync(_engineer, order)).StatusCode);

        var rewrite = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE work_order_replacement SET returned_at_utc = NULL, returned_by_user_id = NULL WHERE work_order_id = {0}", order));
        Assert.Contains("can no longer be changed", rewrite.ToString(), StringComparison.Ordinal);

        var delete = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "DELETE FROM work_order_replacement WHERE work_order_id = {0}", order));
        Assert.Contains("cannot be deleted", delete.ToString(), StringComparison.Ordinal);

        // Postgres wrote the audit rows for it, not the application.
        var audited = await db.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS \"Value\" FROM audit_log WHERE table_name = 'work_order_replacement'").SingleAsync();
        Assert.True(audited >= 2);
    }
}
