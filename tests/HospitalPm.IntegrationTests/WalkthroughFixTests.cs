using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// What walking the finished software with a script turned up, each pinned so it stays fixed.
///
/// None of these were found by the existing tests, because each needs something a test written from the
/// specification does not do: eight people acting in the same instant, text with a NUL in it, a 500-character
/// asset tag, a CSV renamed to .xlsx. The ordinary paths were right; the edges were not.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class WalkthroughFixTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "Walkthrough2026!";

    private ApiFactory _factory = null!;
    private HttpClient _head = null!;
    private string _suffix = null!;
    private int _typeId;
    private int _roomId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var room = new Location { Code = $"WK-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();
            _roomId = room.Id;
            _typeId = (await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator")).Id;
        }

        _head = await SignedInAsync($"wk-head-{_suffix}", Roles.BmeHead);
    }

    private async Task<HttpClient> SignedInAsync(string userName, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser { UserName = userName, FullName = userName, IsActive = true };
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

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _head?.Dispose();
        _factory?.Dispose();
    }

    private async Task<int> MachineAsync(string kind, EquipmentStatus status = EquipmentStatus.InService)
    {
        await using var db = fixture.CreateContext();
        var machine = new Domain.Assets.Equipment
        {
            AssetTag = $"WK-{kind}-{_suffix}", EquipmentTypeId = _typeId, LocationId = _roomId, Status = status,
        };
        db.Equipment.Add(machine);
        await db.SaveChangesAsync();
        return machine.Id;
    }

    private async Task<int> OpenWorkOrderAsync(int machineId)
    {
        var raised = await _head.PostAsJsonAsync("/api/work-orders", new { equipmentId = machineId, faultDescription = "Will not start", priority = 20 });
        Assert.Equal(HttpStatusCode.Created, raised.StatusCode);
        var id = (await raised.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var started = await _head.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = 30 });
        Assert.Equal(HttpStatusCode.NoContent, started.StatusCode);
        return id;
    }

    private async Task<int> PartAsync(string number, int onHand)
    {
        var made = await _head.PostAsJsonAsync("/api/spare-parts", new
        {
            partNumber = $"{number}-{_suffix}", name = "Fuse", unit = "pcs", quantityOnHand = onHand, unitCost = 10.5m,
        });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        return (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<int> OnHandAsync(int partId)
    {
        await using var db = fixture.CreateContext();
        return await db.SpareParts.AsNoTracking().Where(p => p.Id == partId).Select(p => p.QuantityOnHand).SingleAsync();
    }

    // ------------------------------------------------------------- the shelf, when two people take the last one

    [Fact]
    public async Task Twelve_people_taking_a_part_at_once_take_exactly_what_is_on_the_shelf()
    {
        var machine = await MachineAsync("S");
        var order = await OpenWorkOrderAsync(machine);
        var part = await PartAsync("RACE", onHand: 5);

        // Each read the stock as 5, subtracted one and wrote 4. Eight of eight succeeded and the shelf lost one.
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            _head.PostAsJsonAsync($"/api/work-orders/{order}/parts", new { sparePartId = part, quantityUsed = 1 })));

        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        Assert.Equal(7, results.Count(r => r.StatusCode == HttpStatusCode.BadRequest));

        Assert.Equal(0, await OnHandAsync(part));

        await using var db = fixture.CreateContext();
        Assert.Equal(5, await db.WorkOrderParts.CountAsync(p => p.WorkOrderId == order));
    }

    [Fact]
    public async Task Taking_more_than_is_left_is_refused_and_says_how_many_are_left()
    {
        var order = await OpenWorkOrderAsync(await MachineAsync("T"));
        var part = await PartAsync("OVER", onHand: 2);

        var refused = await _head.PostAsJsonAsync($"/api/work-orders/{order}/parts", new { sparePartId = part, quantityUsed = 3 });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Only 2 pcs", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(2, await OnHandAsync(part));
    }

    [Fact]
    public async Task Undoing_a_use_puts_exactly_that_many_back_on_the_shelf()
    {
        var order = await OpenWorkOrderAsync(await MachineAsync("U"));
        var part = await PartAsync("UNDO", onHand: 6);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _head.PostAsJsonAsync($"/api/work-orders/{order}/parts", new { sparePartId = part, quantityUsed = 4 })).StatusCode);
        Assert.Equal(2, await OnHandAsync(part));

        int usage;
        await using (var db = fixture.CreateContext())
        {
            usage = await db.WorkOrderParts.Where(p => p.WorkOrderId == order).Select(p => p.Id).SingleAsync();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await _head.DeleteAsync($"/api/work-orders/{order}/parts/{usage}")).StatusCode);
        Assert.Equal(6, await OnHandAsync(part));
    }

    // ------------------------------------------------------------- two people on the same service request

    [Fact]
    public async Task Six_people_resolving_the_same_service_request_at_once_resolve_it_once()
    {
        var order = await OpenWorkOrderAsync(await MachineAsync("R"));

        // Each read it as "in progress", each passed the check that resolving was allowed, and each wrote:
        // several 200s, and a note for every one.
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            _head.PostAsJsonAsync($"/api/work-orders/{order}/resolve", new { resolutionNotes = $"Replaced the board ({i})" })));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.WorkOrderNotes.CountAsync(n => n.WorkOrderId == order && n.Body.StartsWith("Replaced the board")));
    }

    [Fact]
    public async Task One_person_cancelling_while_another_resolves_leaves_one_outcome_not_both()
    {
        var order = await OpenWorkOrderAsync(await MachineAsync("X"));

        var cancel = _head.PostAsJsonAsync($"/api/work-orders/{order}/status", new { status = 70, note = "Not needed" });
        var resolve = _head.PostAsJsonAsync($"/api/work-orders/{order}/resolve", new { resolutionNotes = "Fixed it" });
        var results = await Task.WhenAll(cancel, resolve);

        // Exactly one of the two went through; the other was told the request is no longer open to that.
        Assert.Equal(1, results.Count(r => r.IsSuccessStatusCode));
        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        await using var db = fixture.CreateContext();
        var final = await db.WorkOrders.AsNoTracking().Where(w => w.Id == order).Select(w => w.Status).SingleAsync();
        var winner = results[0].IsSuccessStatusCode
            ? Domain.WorkOrders.WorkOrderStatus.Cancelled
            : Domain.WorkOrders.WorkOrderStatus.Resolved;
        Assert.Equal(winner, final);
    }

    // ------------------------------------------------------------- In progress takes the fault

    private async Task<(HttpClient Client, int Id)> EngineerAsync(string name)
    {
        var userName = $"wk-{name}-{_suffix}";
        var client = await SignedInAsync(userName, Roles.BmeEngineer);
        await using var db = fixture.CreateContext();
        return (client, await db.Users.AsNoTracking().Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync());
    }

    private async Task<int> RaisedAsync(string kind)
    {
        var raised = await _head.PostAsJsonAsync("/api/work-orders", new { equipmentId = await MachineAsync(kind), faultDescription = "Will not start", priority = 20 });
        Assert.Equal(HttpStatusCode.Created, raised.StatusCode);
        return (await raised.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> DetailAsync(HttpClient who, int order)
        => await who.GetFromJsonAsync<JsonElement>($"/api/work-orders/{order}");

    [Fact]
    public async Task Pressing_In_progress_on_a_fault_nobody_has_takes_it()
    {
        var (anil, anilId) = await EngineerAsync("anil");
        var order = await RaisedAsync("P1");

        var pressed = await anil.PostAsJsonAsync($"/api/work-orders/{order}/status", new { status = 30 });
        Assert.Equal(HttpStatusCode.NoContent, pressed.StatusCode);

        var detail = await DetailAsync(anil, order);
        Assert.Equal(anilId, detail.GetProperty("assignedToUserId").GetInt32());
        Assert.Equal(30, detail.GetProperty("status").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("startedAtUtc").ValueKind);
        Assert.Contains(detail.GetProperty("notes").EnumerateArray(), n => n.GetProperty("body").GetString()!.StartsWith("Taken by"));
    }

    [Fact]
    public async Task Of_two_people_pressing_In_progress_at_the_same_moment_the_first_gets_it_and_the_other_is_told_who()
    {
        var (anil, anilId) = await EngineerAsync("a");
        var (sunita, sunitaId) = await EngineerAsync("s");
        var order = await RaisedAsync("P2");

        var results = await Task.WhenAll(
            anil.PostAsJsonAsync($"/api/work-orders/{order}/status", new { status = 30 }),
            sunita.PostAsJsonAsync($"/api/work-orders/{order}/status", new { status = 30 }));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.NoContent));
        var refused = Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("is already working on this", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        var winner = results[0].StatusCode == HttpStatusCode.NoContent ? anilId : sunitaId;
        Assert.Equal(winner, (await DetailAsync(_head, order)).GetProperty("assignedToUserId").GetInt32());
    }

    [Fact]
    public async Task A_fault_given_to_one_engineer_can_only_be_started_by_that_engineer()
    {
        var (anil, anilId) = await EngineerAsync("own");
        var (sunita, _) = await EngineerAsync("other");
        var order = await RaisedAsync("P3");

        Assert.Equal(HttpStatusCode.NoContent,
            (await _head.PostAsJsonAsync($"/api/work-orders/{order}/assign", new { assignedToUserId = anilId })).StatusCode);

        // The other engineer is not offered the button, and is refused if they press it anyway.
        var offered = (await DetailAsync(sunita, order)).GetProperty("allowedTransitions").EnumerateArray().Select(t => t.GetInt32());
        Assert.DoesNotContain(30, offered);
        var offeredToOwner = (await DetailAsync(anil, order)).GetProperty("allowedTransitions").EnumerateArray().Select(t => t.GetInt32());
        Assert.Contains(30, offeredToOwner);

        var refused = await sunita.PostAsJsonAsync($"/api/work-orders/{order}/status", new { status = 30 });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("only they can start it", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(20, (await DetailAsync(_head, order)).GetProperty("status").GetInt32());

        // The Head is held to it too: the way to hand it on is to reassign it.
        Assert.Equal(HttpStatusCode.Conflict,
            (await _head.PostAsJsonAsync($"/api/work-orders/{order}/status", new { status = 30 })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await anil.PostAsJsonAsync($"/api/work-orders/{order}/status", new { status = 30 })).StatusCode);
    }

    [Fact]
    public async Task Once_one_engineer_is_working_on_a_fault_another_is_told_so_and_it_stays_theirs()
    {
        var (anil, anilId) = await EngineerAsync("w1");
        var (sunita, _) = await EngineerAsync("w2");
        var order = await RaisedAsync("P4");
        await anil.PostAsJsonAsync($"/api/work-orders/{order}/status", new { status = 30 });

        var refused = await sunita.PostAsJsonAsync($"/api/work-orders/{order}/status", new { status = 30 });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("already working on this", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        Assert.Equal(anilId, (await DetailAsync(_head, order)).GetProperty("assignedToUserId").GetInt32());
    }

    [Fact]
    public async Task Reopening_a_resolved_fault_is_not_taking_it_and_stays_open_to_anyone_who_may_work()
    {
        var (anil, anilId) = await EngineerAsync("r1");
        var order = await RaisedAsync("P5");
        await anil.PostAsJsonAsync($"/api/work-orders/{order}/status", new { status = 30 });
        await anil.PostAsJsonAsync($"/api/work-orders/{order}/resolve", new { resolutionNotes = "Replaced the board" });

        var reopened = await _head.PostAsJsonAsync($"/api/work-orders/{order}/status", new { status = 30, note = "Fault is back" });

        Assert.Equal(HttpStatusCode.NoContent, reopened.StatusCode);
        Assert.Equal(anilId, (await DetailAsync(_head, order)).GetProperty("assignedToUserId").GetInt32());
    }

    // ------------------------------------------------------------- what the database refuses is a 400 or 409, not a 500

    [Fact]
    public async Task A_NUL_character_in_a_fault_is_refused_with_a_message_not_a_server_error()
    {
        var machine = await MachineAsync("N");

        var raised = await _head.PostAsJsonAsync("/api/work-orders", new { equipmentId = machine, faultDescription = "a\u0000b", priority = 20 });

        Assert.Equal(HttpStatusCode.BadRequest, raised.StatusCode);
        Assert.Contains("character", (await raised.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_asset_tag_that_is_far_too_long_is_refused_with_a_message_not_a_server_error()
    {
        var made = await _head.PostAsJsonAsync("/api/equipment", new
        {
            assetTag = new string('T', 500), equipmentTypeId = _typeId, locationId = _roomId, status = 20,
        });

        Assert.Equal(HttpStatusCode.BadRequest, made.StatusCode);
        Assert.Contains("too long", (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_number_too_large_for_the_column_is_refused_with_a_message_not_a_server_error()
    {
        var made = await _head.PostAsJsonAsync("/api/spare-parts", new
        {
            partNumber = $"BIG-{_suffix}", name = "Fuse", unit = "pcs", quantityOnHand = 1, unitCost = 1e20,
        });

        Assert.Equal(HttpStatusCode.BadRequest, made.StatusCode);
        Assert.Contains("too large", (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Six_people_adding_the_same_asset_tag_at_once_leave_one_machine_and_five_conflicts()
    {
        var tag = $"WK-RACE-{_suffix}";

        // Each passed the "is it taken?" check before any had saved, and the losers of the database's unique
        // index got a bare 500 instead of being told someone had just done it.
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            _head.PostAsJsonAsync("/api/equipment", new { assetTag = tag, equipmentTypeId = _typeId, locationId = _roomId, status = 20 })));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(5, results.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        Assert.DoesNotContain(results, r => (int)r.StatusCode >= 500);

        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.Equipment.CountAsync(e => e.AssetTag == tag));
    }

    [Fact]
    public async Task A_location_code_far_too_long_is_refused_with_a_message_not_a_server_error()
    {
        var made = await _head.PostAsJsonAsync("/api/locations", new { code = new string('Z', 300), name = "Too long", level = (int)LocationLevel.Room });

        Assert.Equal(HttpStatusCode.BadRequest, made.StatusCode);
    }

    [Fact]
    public async Task An_account_made_or_reset_with_no_password_is_refused_not_a_server_error()
    {
        var none = await _head.PostAsJsonAsync("/api/users", new { userName = $"wk-nopw-{_suffix}", fullName = "No Password", role = Roles.BmeEngineer, password = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);

        var made = await _head.PostAsJsonAsync("/api/users", new { userName = $"wk-pw-{_suffix}", fullName = "Has Password", role = Roles.BmeEngineer, password = "Hospital@2026" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var id = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var reset = await _head.PostAsJsonAsync($"/api/users/{id}/reset-password", new { password = (string?)null });
        Assert.Equal(HttpStatusCode.BadRequest, reset.StatusCode);
    }

    // ------------------------------------------------------------- a machine that has been condemned has no faults

    [Fact]
    public async Task A_fault_cannot_be_reported_on_a_condemned_machine()
    {
        var machine = await MachineAsync("C", EquipmentStatus.Condemned);

        var raised = await _head.PostAsJsonAsync("/api/work-orders", new { equipmentId = machine, faultDescription = "Dead", priority = 20 });

        Assert.Equal(HttpStatusCode.Conflict, raised.StatusCode);
        Assert.Contains("condemned", (await raised.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        await using var db = fixture.CreateContext();
        Assert.False(await db.WorkOrders.AnyAsync(w => w.EquipmentId == machine));
    }

    // ------------------------------------------------------------- tags and dates that cannot be right

    private object Machine(string tag, string? purchase = null, string? install = null, string? warranty = null, string? notes = null) => new
    {
        assetTag = tag, equipmentTypeId = _typeId, locationId = _roomId, status = 20,
        purchaseDate = purchase, installationDate = install, warrantyExpiryDate = warranty, notes,
    };

    [Fact]
    public async Task A_new_asset_tag_with_a_slash_is_refused_and_says_what_to_use()
    {
        var made = await _head.PostAsJsonAsync("/api/equipment", Machine($"ICU/{_suffix}"));

        Assert.Equal(HttpStatusCode.BadRequest, made.StatusCode);
        Assert.Contains("\"-\"", (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_machine_that_already_has_a_slash_in_its_tag_can_still_be_edited_but_not_given_a_new_one()
    {
        int id;
        await using (var db = fixture.CreateContext())
        {
            var old = new Domain.Assets.Equipment { AssetTag = $"OLD/{_suffix}", EquipmentTypeId = _typeId, LocationId = _roomId };
            db.Equipment.Add(old);
            await db.SaveChangesAsync();
            id = old.Id;
        }

        // An old record is not locked out by a rule it was made before.
        var edited = await _head.PutAsJsonAsync($"/api/equipment/{id}", Machine($"OLD/{_suffix}", notes: "Checked"));
        Assert.Equal(HttpStatusCode.NoContent, edited.StatusCode);

        var renamed = await _head.PutAsJsonAsync($"/api/equipment/{id}", Machine($"NEW/{_suffix}"));
        Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);
    }

    [Theory]
    [InlineData("2090-01-01", null, null, "future")]
    [InlineData("2024-06-01", "2024-01-01", null, "installed")]
    [InlineData("2024-06-01", null, "2024-01-01", "warranty")]
    public async Task Dates_that_cannot_be_true_are_refused(string purchase, string? install, string? warranty, string label)
    {
        var made = await _head.PostAsJsonAsync("/api/equipment", Machine($"WK-D-{label}-{_suffix}", purchase, install, warranty));

        Assert.Equal(HttpStatusCode.BadRequest, made.StatusCode);
    }

    [Fact]
    public async Task Blank_dates_and_ordinary_dates_are_fine()
    {
        Assert.Equal(HttpStatusCode.Created,
            (await _head.PostAsJsonAsync("/api/equipment", Machine($"WK-D1-{_suffix}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await _head.PostAsJsonAsync("/api/equipment", Machine($"WK-D2-{_suffix}", "2024-01-01", "2024-02-01", "2027-01-01"))).StatusCode);
    }

    [Fact]
    public async Task A_machine_whose_dates_were_already_wrong_can_still_have_its_notes_changed()
    {
        int id;
        await using (var db = fixture.CreateContext())
        {
            var old = new Domain.Assets.Equipment
            {
                AssetTag = $"WK-OLD-{_suffix}", EquipmentTypeId = _typeId, LocationId = _roomId,
                PurchaseDate = new DateOnly(2024, 6, 1), InstallationDate = new DateOnly(2024, 1, 1),
            };
            db.Equipment.Add(old);
            await db.SaveChangesAsync();
            id = old.Id;
        }

        var notesOnly = await _head.PutAsJsonAsync($"/api/equipment/{id}", Machine($"WK-OLD-{_suffix}", "2024-06-01", "2024-01-01", notes: "Serviced"));
        Assert.Equal(HttpStatusCode.NoContent, notesOnly.StatusCode);

        // Touching the dates is where it is checked again.
        var moved = await _head.PutAsJsonAsync($"/api/equipment/{id}", Machine($"WK-OLD-{_suffix}", "2024-06-01", "2024-02-01"));
        Assert.Equal(HttpStatusCode.BadRequest, moved.StatusCode);
    }

    // ------------------------------------------------------------- the same person signing in twice at once

    [Fact]
    public async Task Eight_simultaneous_sign_ins_as_the_same_person_all_succeed()
    {
        // Found by the stress test: the sign-in saved the whole user row, which is checked against the row's version, so
        // the loser of two overlapping sign-ins (two devices, a second tab) got an uncaught concurrency error: a 500 for
        // seven of eight in a test.
        var userName = $"wk-twice-{_suffix}";
        await SignedInAsync(userName, Roles.BmeEngineer);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            using var client = _factory.CreateClient();
            return await client.PostAsJsonAsync("/api/auth/login", new { userName, password = Password });
        }));

        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        await using var db = fixture.CreateContext();
        Assert.NotNull(await db.Users.AsNoTracking().Where(u => u.UserName == userName).Select(u => u.LastLoginAtUtc).SingleAsync());
    }

    // ------------------------------------------------------------- an address that does not exist

    [Theory]
    [InlineData("/api/nonexistent")]
    [InlineData("/api/equipment/abc")]
    [InlineData("/api/work-orders/%7B%7BwoId%7D%7D")]
    [InlineData("/api/reports/nothing/here.pdf")]
    public async Task An_api_address_that_does_not_exist_is_a_404_not_the_front_page(string address)
    {
        // The catch-all that serves the web app answered these with 200 and index.html, so a client with a mistyped
        // address, or an unfilled {{variable}}, was told it had worked.
        var response = await _head.GetAsync(address);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain("html", response.Content.Headers.ContentType?.MediaType ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_web_page_address_is_not_caught_by_the_api_404()
    {
        // Only /api is excluded: a browser's deep link or refresh on a screen goes to the web app, which serves
        // index.html (in a test host with no built screens there is simply no file, and no API message either).
        var response = await _head.GetAsync("/equipment/123");
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("There is no such address in the API", body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------- choosing the wrong file to import

    public static TheoryData<string, byte[]> NotWorkbooks() => new()
    {
        { "plain text", Encoding.UTF8.GetBytes("this is not an Excel file at all") },
        { "a CSV renamed .xlsx", Encoding.UTF8.GetBytes("asset_tag,location\nA1,ICU\nA2,ICU\n") },
        { "a zip with nothing in it", [0x50, 0x4B, 0x03, 0x04, .. new byte[200]] },
    };

    [Theory]
    [MemberData(nameof(NotWorkbooks))]
    public async Task A_file_that_is_not_a_workbook_is_refused_by_every_import_step_not_a_server_error(string label, byte[] bytes)
    {
        foreach (var path in new[]
        {
            "/api/equipment/import/validate", "/api/equipment/import/report", "/api/equipment/import/commit",
            "/api/equipment/import/locations/validate", "/api/equipment/import/locations/report", "/api/equipment/import/locations/commit",
        })
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            form.Add(file, "file", "register.xlsx");

            var response = await _head.PostAsync(path, form);

            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{label} to {path} gave {(int)response.StatusCode}, not 400");
        }
    }
}
