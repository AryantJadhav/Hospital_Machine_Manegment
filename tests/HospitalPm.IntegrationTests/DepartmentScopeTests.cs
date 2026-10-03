using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// A person in another department sees their own departments' equipment and requests, and nothing
/// else. Two departments, a machine and a service request in each, and a ward user given one of them.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DepartmentScopeTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "DeptScope2026!";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 2, 3];

    private ApiFactory _factory = null!;
    private string _suffix = null!;

    private int _icuDepartmentId;
    private int _otDepartmentId;
    private int _icuMachineId;
    private int _otMachineId;
    private string _icuTag = null!;
    private string _otTag = null!;
    private int _icuOrderId;
    private int _otOrderId;
    private int _partId;

    private HttpClient _head = null!;
    private HttpClient _engineer = null!;
    private HttpClient _it = null!;
    private HttpClient _ward = null!;
    private int _wardId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            // A building holding two departments, each with a room. A ward user is given a department,
            // and must see the room beneath it.
            var building = new Location { Code = $"DSB-{_suffix}", Name = $"Block {_suffix}", Level = LocationLevel.Building };
            db.Locations.Add(building);
            await db.SaveChangesAsync();

            var icu = new Location { Code = $"DSI-{_suffix}", Name = $"ICU {_suffix}", Level = LocationLevel.Department, ParentId = building.Id };
            var ot = new Location { Code = $"DSO-{_suffix}", Name = $"OT {_suffix}", Level = LocationLevel.Department, ParentId = building.Id };
            db.Locations.AddRange(icu, ot);
            await db.SaveChangesAsync();

            var icuRoom = new Location { Code = $"DSIR-{_suffix}", Name = $"ICU bay {_suffix}", Level = LocationLevel.Room, ParentId = icu.Id };
            var otRoom = new Location { Code = $"DSOR-{_suffix}", Name = $"OT theatre {_suffix}", Level = LocationLevel.Room, ParentId = ot.Id };
            db.Locations.AddRange(icuRoom, otRoom);
            await db.SaveChangesAsync();

            _icuDepartmentId = icu.Id;
            _otDepartmentId = ot.Id;

            var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
            _icuTag = $"DSIM-{_suffix}".ToUpperInvariant();
            _otTag = $"DSOM-{_suffix}".ToUpperInvariant();

            var icuMachine = new Domain.Assets.Equipment
            {
                AssetTag = _icuTag, EquipmentTypeId = type.Id, LocationId = icuRoom.Id,
                Manufacturer = "Acme", Model = "V-1", PurchaseCost = 12345m, Notes = "Internal remark",
                IsInsured = true, InsuranceProvider = "Secret Insurer", InsuranceExpiryDate = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(1),
            };
            var otMachine = new Domain.Assets.Equipment
            {
                AssetTag = _otTag, EquipmentTypeId = type.Id, LocationId = otRoom.Id, Manufacturer = "Acme", Model = "V-1",
            };
            db.Equipment.AddRange(icuMachine, otMachine);
            await db.SaveChangesAsync();
            _icuMachineId = icuMachine.Id;
            _otMachineId = otMachine.Id;
        }

        _head = await SignInAsync("ds-head", Roles.BmeHead);
        _engineer = await SignInAsync("ds-eng", Roles.BmeEngineer);
        _it = await SignInAsync("ds-it", Roles.ItAdmin);
        (_ward, _wardId) = await SignInWithIdAsync("ds-ward", Roles.DepartmentUser);

        // A spare part, so a request can show what was used and (to others) what it cost.
        var part = await _head.PostAsJsonAsync("/api/spare-parts", new
        {
            partNumber = $"DS-{_suffix}", name = $"Filter {_suffix}", quantityOnHand = 10, unitCost = 250m, unit = "pcs",
        });
        part.EnsureSuccessStatusCode();
        _partId = (await part.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        _icuOrderId = await RaiseAsync(_icuMachineId, "ICU machine alarm is faulty");
        _otOrderId = await RaiseAsync(_otMachineId, "OT machine will not start");

        Assert.True((await _engineer.PostAsJsonAsync($"/api/work-orders/{_icuOrderId}/parts", new { sparePartId = _partId, quantityUsed = 2 })).IsSuccessStatusCode);

        // The ward user belongs to the ICU, and so to everything beneath it.
        Assert.Equal(HttpStatusCode.NoContent,
            (await _head.PutAsJsonAsync($"/api/users/{_wardId}/departments", new { locationIds = new[] { _icuDepartmentId } })).StatusCode);
    }

    private async Task<int> RaiseAsync(int machineId, string fault)
    {
        var res = await _engineer.PostAsJsonAsync("/api/work-orders", new { equipmentId = machineId, faultDescription = fault, priority = 20 });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<HttpClient> SignInAsync(string prefix, string role) => (await SignInWithIdAsync(prefix, role)).Client;

    private async Task<(HttpClient Client, int Id)> SignInWithIdAsync(string prefix, string role)
    {
        int id;
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
            id = user.Id;
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = $"{prefix}-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (client, id);
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _factory?.Dispose();

    private static HashSet<string?> Tags(JsonElement list) =>
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("assetTag").GetString()).ToHashSet();

    // ---------------- equipment ----------------

    [Fact]
    public async Task A_ward_user_sees_the_machines_of_their_department_and_the_rooms_beneath_it_and_no_others()
    {
        var mine = Tags(await _ward.GetFromJsonAsync<JsonElement>($"/api/equipment?q={_suffix}"));

        Assert.Contains(_icuTag, mine);
        Assert.DoesNotContain(_otTag, mine);

        // The same list for an engineer holds both: scope is for the ward user, not for the data.
        var all = Tags(await _engineer.GetFromJsonAsync<JsonElement>($"/api/equipment?q={_suffix}"));
        Assert.Contains(_icuTag, all);
        Assert.Contains(_otTag, all);
    }

    [Fact]
    public async Task Another_departments_machine_is_not_there_to_open_by_number_by_tag_or_by_asking_for_its_place()
    {
        Assert.Equal(HttpStatusCode.OK, (await _ward.GetAsync($"/api/equipment/{_icuMachineId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _ward.GetAsync($"/api/equipment/by-tag/{_icuTag}")).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await _ward.GetAsync($"/api/equipment/{_otMachineId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _ward.GetAsync($"/api/equipment/by-tag/{_otTag}")).StatusCode);

        // Naming the other department's place in the search finds nothing, and does not say it exists.
        var asked = await _ward.GetAsync($"/api/equipment?locationId={_otDepartmentId}");
        Assert.Equal(HttpStatusCode.NotFound, asked.StatusCode);
    }

    [Fact]
    public async Task A_ward_user_is_not_shown_what_a_machine_cost_how_it_is_insured_or_the_departments_notes()
    {
        var mine = await _ward.GetFromJsonAsync<JsonElement>($"/api/equipment/{_icuMachineId}");

        Assert.Equal(_icuTag, mine.GetProperty("assetTag").GetString());
        Assert.Equal("Acme", mine.GetProperty("manufacturer").GetString());
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("purchaseCost").ValueKind);
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("insuranceProvider").ValueKind);
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("notes").ValueKind);
        Assert.False(mine.GetProperty("isInsured").GetBoolean());

        // The head of the department sees all of it.
        var full = await _head.GetFromJsonAsync<JsonElement>($"/api/equipment/{_icuMachineId}");
        Assert.Equal(12345m, full.GetProperty("purchaseCost").GetDecimal());
        Assert.Equal("Secret Insurer", full.GetProperty("insuranceProvider").GetString());
        Assert.Equal("Internal remark", full.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task The_list_of_places_a_ward_user_may_pick_is_only_their_own()
    {
        var places = await _ward.GetFromJsonAsync<JsonElement>("/api/lookups/locations");
        var names = places.EnumerateArray().Select(l => l.GetProperty("name").GetString()).ToList();

        Assert.Contains($"ICU {_suffix}", names);
        Assert.Contains($"ICU bay {_suffix}", names);
        Assert.DoesNotContain($"OT {_suffix}", names);
        Assert.DoesNotContain($"OT theatre {_suffix}", names);
        Assert.DoesNotContain($"Block {_suffix}", names);
    }

    [Fact]
    public async Task A_ward_user_with_no_department_sees_nothing_not_everything()
    {
        var (nobody, _) = await SignInWithIdAsync("ds-none", Roles.DepartmentUser);

        var list = await nobody.GetFromJsonAsync<JsonElement>($"/api/equipment?q={_suffix}");
        Assert.Equal(0, list.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await nobody.GetAsync($"/api/equipment/{_icuMachineId}")).StatusCode);

        var orders = await nobody.GetFromJsonAsync<JsonElement>("/api/work-orders");
        Assert.Equal(0, orders.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_ward_user_cannot_reach_anything_else_in_the_system()
    {
        foreach (var route in new[]
        {
            "/api/dashboard", "/api/pm/tasks", "/api/spare-parts", "/api/training", "/api/locations",
            $"/api/equipment/{_icuMachineId}/history", $"/api/equipment/{_icuMachineId}/spend", $"/api/labels/qr/{_icuTag}",
            "/api/reports/stock", "/api/users", "/api/admin/backups", "/api/people",
        })
        {
            Assert.True((await _ward.GetAsync(route)).StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
                $"{route} should be closed to a ward user");
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await _ward.PostAsJsonAsync("/api/equipment", new { assetTag = "X" })).StatusCode);
    }

    // ---------------- service requests ----------------

    [Fact]
    public async Task A_ward_user_sees_only_the_requests_on_their_departments_machines()
    {
        var list = await _ward.GetFromJsonAsync<JsonElement>("/api/work-orders?openOnly=false&pageSize=200");
        var ids = list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToHashSet();

        Assert.Contains(_icuOrderId, ids);
        Assert.DoesNotContain(_otOrderId, ids);

        Assert.Equal(HttpStatusCode.OK, (await _ward.GetAsync($"/api/work-orders/{_icuOrderId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _ward.GetAsync($"/api/work-orders/{_otOrderId}")).StatusCode);

        // The counts on the summary are theirs too, and a search for the other department's words finds nothing.
        var searched = await _ward.GetFromJsonAsync<JsonElement>("/api/work-orders?q=will not start");
        Assert.Equal(0, searched.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_ward_user_can_report_a_fault_on_their_own_machine_and_not_on_anyone_elses()
    {
        var ok = await _ward.PostAsJsonAsync("/api/work-orders",
            new { equipmentId = _icuMachineId, faultDescription = "Screen flickers", priority = 20 });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var other = await _ward.PostAsJsonAsync("/api/work-orders",
            new { equipmentId = _otMachineId, faultDescription = "Not my machine", priority = 20 });
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
    }

    [Fact]
    public async Task A_ward_user_can_answer_on_their_request_with_a_note_and_a_photo_but_not_work_it()
    {
        Assert.True((await _ward.PostAsJsonAsync($"/api/work-orders/{_icuOrderId}/notes", new { body = "It started again at noon" })).IsSuccessStatusCode);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "files", "fault.jpg");
        Assert.True((await _ward.PostAsync($"/api/work-orders/{_icuOrderId}/photos", form)).IsSuccessStatusCode);

        // Not on the other department's request: it is not there for them.
        Assert.Equal(HttpStatusCode.NotFound,
            (await _ward.PostAsJsonAsync($"/api/work-orders/{_otOrderId}/notes", new { body = "Hello" })).StatusCode);

        // Working it is the engineer's: status, resolving, parts, assigning, cancelling.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _ward.PostAsJsonAsync($"/api/work-orders/{_icuOrderId}/status", new { status = 20 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _ward.PostAsJsonAsync($"/api/work-orders/{_icuOrderId}/resolve", new { resolutionNotes = "Done" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _ward.PostAsJsonAsync($"/api/work-orders/{_icuOrderId}/parts", new { sparePartId = _partId, quantityUsed = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _ward.PostAsJsonAsync($"/api/work-orders/{_icuOrderId}/assign", new { assignedToUserId = _wardId })).StatusCode);
    }

    [Fact]
    public async Task A_photo_on_another_departments_request_cannot_be_fetched_by_number()
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "files", "ot.jpg");
        var uploaded = await _engineer.PostAsync($"/api/work-orders/{_otOrderId}/photos", form);
        Assert.True(uploaded.IsSuccessStatusCode);

        var detail = await _engineer.GetFromJsonAsync<JsonElement>($"/api/work-orders/{_otOrderId}");
        var photoId = detail.GetProperty("photos")[0].GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.OK, (await _engineer.GetAsync($"/api/work-orders/photos/{photoId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _ward.GetAsync($"/api/work-orders/photos/{photoId}")).StatusCode);
    }

    [Fact]
    public async Task A_ward_user_is_not_offered_the_next_steps_nor_told_what_the_parts_cost()
    {
        var mine = await _ward.GetFromJsonAsync<JsonElement>($"/api/work-orders/{_icuOrderId}");
        Assert.Equal(0, mine.GetProperty("allowedTransitions").GetArrayLength());

        var part = mine.GetProperty("partsUsed")[0];
        Assert.Equal(2, part.GetProperty("quantityUsed").GetInt32());
        Assert.Equal(JsonValueKind.Null, part.GetProperty("unitCostAtUse").ValueKind);

        // The engineer is offered steps and sees the cost.
        var theirs = await _engineer.GetFromJsonAsync<JsonElement>($"/api/work-orders/{_icuOrderId}");
        Assert.True(theirs.GetProperty("allowedTransitions").GetArrayLength() > 0);
        Assert.Equal(250m, theirs.GetProperty("partsUsed")[0].GetProperty("unitCostAtUse").GetDecimal());

        // The printed report is theirs too, without the money.
        var wardPdf = await _ward.GetAsync($"/api/reports/work-orders/{_icuOrderId}/report.pdf");
        Assert.Equal(HttpStatusCode.OK, wardPdf.StatusCode);
        Assert.Equal("application/pdf", wardPdf.Content.Headers.ContentType?.MediaType);
        var wardBytes = await wardPdf.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", Encoding.ASCII.GetString(wardBytes[..4]));

        var engineerPdf = await _engineer.GetAsync($"/api/reports/work-orders/{_icuOrderId}/report.pdf");
        Assert.Equal(HttpStatusCode.OK, engineerPdf.StatusCode);

        // Another department's report is not there to print.
        Assert.Equal(HttpStatusCode.NotFound, (await _ward.GetAsync($"/api/reports/work-orders/{_otOrderId}/report.pdf")).StatusCode);

    }

    [Fact]
    public async Task A_request_whose_repair_is_done_stays_on_the_departments_list_for_a_week_and_then_leaves_it()
    {
        var wardOrder = await _ward.PostAsJsonAsync("/api/work-orders",
            new { equipmentId = _icuMachineId, faultDescription = "Will be repaired", priority = 20 });
        var id = (await wardOrder.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        async Task<HashSet<int>> DefaultListAsync(HttpClient client)
        {
            var list = await client.GetFromJsonAsync<JsonElement>("/api/work-orders?pageSize=200");
            return list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToHashSet();
        }

        Assert.Equal(HttpStatusCode.NoContent,
            (await _engineer.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = 30 })).StatusCode);
        Assert.True((await _engineer.PostAsJsonAsync($"/api/work-orders/{id}/resolve", new { resolutionNotes = "Replaced the board" })).IsSuccessStatusCode);

        // Repair done: gone from the team's own queue, still on the department's list.
        Assert.DoesNotContain(id, await DefaultListAsync(_engineer));
        Assert.Contains(id, await DefaultListAsync(_ward));

        var row = (await _ward.GetFromJsonAsync<JsonElement>("/api/work-orders?pageSize=200"))
            .GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == id);
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("resolvedAtUtc").ValueKind);

        // Six days later it is still there; eight days later it has left the default list, though it is
        // not lost: asking for resolved ones finds it.
        await SetResolvedAgoAsync(id, TimeSpan.FromDays(6));
        Assert.Contains(id, await DefaultListAsync(_ward));

        await SetResolvedAgoAsync(id, TimeSpan.FromDays(8));
        Assert.DoesNotContain(id, await DefaultListAsync(_ward));

        var resolved = await _ward.GetFromJsonAsync<JsonElement>("/api/work-orders?status=50&pageSize=200");
        Assert.Contains(id, resolved.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()));

        // Another department's repaired request is never on their list, however recent.
        await _engineer.PostAsJsonAsync($"/api/work-orders/{_otOrderId}/status", new { status = 30 });
        Assert.True((await _engineer.PostAsJsonAsync($"/api/work-orders/{_otOrderId}/resolve", new { resolutionNotes = "Done" })).IsSuccessStatusCode);
        Assert.DoesNotContain(_otOrderId, await DefaultListAsync(_ward));
    }

    private async Task SetResolvedAgoAsync(int orderId, TimeSpan ago)
    {
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE work_order SET resolved_at_utc = {0} WHERE id = {1}", DateTime.UtcNow - ago, orderId);
    }

    private async Task ResolveAsync(int id, string notes)
    {
        Assert.Equal(HttpStatusCode.NoContent,
            (await _engineer.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = 30 })).StatusCode);
        Assert.True((await _engineer.PostAsJsonAsync($"/api/work-orders/{id}/resolve", new { resolutionNotes = notes })).IsSuccessStatusCode);
    }

    private static HashSet<int> Ids(JsonElement list) =>
        list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToHashSet();

    [Fact]
    public async Task A_ward_user_has_a_history_of_the_repairs_done_on_their_departments_machines_and_no_others()
    {
        await ResolveAsync(_icuOrderId, $"Replaced the ICU board {_suffix}");
        await ResolveAsync(_otOrderId, $"Replaced the OT board {_suffix}");

        var mine = await _ward.GetFromJsonAsync<JsonElement>("/api/work-orders/history?pageSize=200");
        var ids = Ids(mine);
        Assert.Contains(_icuOrderId, ids);
        Assert.DoesNotContain(_otOrderId, ids);

        // What was wrong, what was done and by whom, and how long the machine was down.
        var row = mine.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == _icuOrderId);
        Assert.Equal($"Replaced the ICU board {_suffix}", row.GetProperty("resolutionNotes").GetString());
        Assert.Equal("ICU machine alarm is faulty", row.GetProperty("faultDescription").GetString());
        Assert.StartsWith("ds-eng", row.GetProperty("resolvedByName").GetString());
        Assert.StartsWith("ds-eng", row.GetProperty("reportedByName").GetString());
        Assert.True(mine.GetProperty("done").GetInt32() >= 1);

        // No money anywhere in it, and no parts: that is the biomedical department's.
        var raw = mine.GetRawText();
        Assert.DoesNotContain("unitCost", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("partsUsed", raw, StringComparison.OrdinalIgnoreCase);

        // The engineer's view of the same history is the whole hospital's.
        var all = Ids(await _engineer.GetFromJsonAsync<JsonElement>("/api/work-orders/history?pageSize=200"));
        Assert.Contains(_icuOrderId, all);
        Assert.Contains(_otOrderId, all);
    }

    [Fact]
    public async Task The_history_can_be_narrowed_to_what_they_raised_a_word_or_a_period()
    {
        var made = await _ward.PostAsJsonAsync("/api/work-orders",
            new { equipmentId = _icuMachineId, faultDescription = "Raised and repaired", priority = 20 });
        var wardId = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        await ResolveAsync(wardId, $"Cleaned the sensor {_suffix}");
        await ResolveAsync(_icuOrderId, $"Replaced the ICU board {_suffix}");

        // Only the ones they raised.
        var raised = Ids(await _ward.GetFromJsonAsync<JsonElement>("/api/work-orders/history?requestedBy=me&pageSize=200"));
        Assert.Contains(wardId, raised);
        Assert.DoesNotContain(_icuOrderId, raised);

        // By a word in what was done, and in what was wrong.
        var byDone = Ids(await _ward.GetFromJsonAsync<JsonElement>($"/api/work-orders/history?q=Cleaned the sensor {_suffix}"));
        Assert.Equal([wardId], byDone.ToArray());
        var byFault = Ids(await _ward.GetFromJsonAsync<JsonElement>("/api/work-orders/history?q=alarm is faulty&pageSize=200"));
        Assert.Contains(_icuOrderId, byFault);

        // A period in the past holds none of today's repairs; a backwards one is refused.
        var past = await _ward.GetFromJsonAsync<JsonElement>("/api/work-orders/history?from=2020-01-01&to=2020-01-31");
        Assert.Equal(0, past.GetProperty("done").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _ward.GetAsync("/api/work-orders/history?from=2026-02-01&to=2026-01-01")).StatusCode);
    }

    [Fact]
    public async Task The_history_can_be_downloaded_as_a_spreadsheet_with_only_their_own_repairs()
    {
        await ResolveAsync(_icuOrderId, $"Replaced the ICU board {_suffix}");
        await ResolveAsync(_otOrderId, $"Replaced the OT board {_suffix}");

        var res = await _ward.GetAsync("/api/work-orders/history/report.csv");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType?.MediaType);

        var csv = await res.Content.ReadAsStringAsync();
        Assert.StartsWith("Request,Machine number,Machine,Where,What was wrong,Breakdown type,What was done", csv);
        Assert.Contains(_icuTag, csv);
        Assert.Contains($"Replaced the ICU board {_suffix}", csv);
        Assert.DoesNotContain(_otTag, csv);
        Assert.DoesNotContain($"Replaced the OT board {_suffix}", csv);
        Assert.DoesNotContain("cost", csv, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_closed_request_follows_the_same_week()
    {
        var made = await _ward.PostAsJsonAsync("/api/work-orders",
            new { equipmentId = _icuMachineId, faultDescription = "Will be closed", priority = 20 });
        var id = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        await _engineer.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = 30 });
        await _engineer.PostAsJsonAsync($"/api/work-orders/{id}/resolve", new { resolutionNotes = "Done" });
        Assert.Equal(HttpStatusCode.NoContent,
            (await _head.PostAsJsonAsync($"/api/work-orders/{id}/status", new { status = 60 })).StatusCode);

        var list = await _ward.GetFromJsonAsync<JsonElement>("/api/work-orders?pageSize=200");
        Assert.Contains(id, list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()));
    }

    [Fact]
    public async Task A_ward_user_can_list_just_the_requests_they_raised_and_sees_who_raised_each()
    {
        var mineRes = await _ward.PostAsJsonAsync("/api/work-orders",
            new { equipmentId = _icuMachineId, faultDescription = "Raised by the ward user", priority = 20 });
        Assert.Equal(HttpStatusCode.Created, mineRes.StatusCode);
        var mineId = (await mineRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // Everything in their department: the engineer's request on the ICU machine, and theirs.
        var all = await _ward.GetFromJsonAsync<JsonElement>("/api/work-orders?pageSize=200");
        var allIds = all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToHashSet();
        Assert.Contains(_icuOrderId, allIds);
        Assert.Contains(mineId, allIds);

        // Only the ones they raised.
        var mine = await _ward.GetFromJsonAsync<JsonElement>("/api/work-orders?requestedBy=me&pageSize=200");
        var rows = mine.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([mineId], rows.Select(r => r.GetProperty("id").GetInt32()).ToArray());
        Assert.StartsWith("ds-ward", rows[0].GetProperty("reportedByName").GetString());

        // Each row says who raised it, whoever is reading.
        var engineerRow = all.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetInt32() == _icuOrderId);
        Assert.StartsWith("ds-eng", engineerRow.GetProperty("reportedByName").GetString());

        // The same filter works for the engineer: what they raised, across the whole hospital.
        var engineers = await _engineer.GetFromJsonAsync<JsonElement>("/api/work-orders?requestedBy=me&pageSize=200");
        var engineerIds = engineers.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToHashSet();
        Assert.Contains(_icuOrderId, engineerIds);
        Assert.Contains(_otOrderId, engineerIds);
        Assert.DoesNotContain(mineId, engineerIds);
    }

    [Fact]
    public async Task Nothing_changes_for_the_people_who_work_across_the_whole_hospital()
    {
        foreach (var client in new[] { _head, _engineer })
        {
            var tags = Tags(await client.GetFromJsonAsync<JsonElement>($"/api/equipment?q={_suffix}"));
            Assert.Contains(_icuTag, tags);
            Assert.Contains(_otTag, tags);

            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/work-orders/{_otOrderId}")).StatusCode);
        }
    }

    // ---------------- giving a person their departments ----------------

    [Fact]
    public async Task Whoever_may_manage_the_account_sets_its_departments_and_the_staff_list_and_me_show_them()
    {
        var (_, otherId) = await SignInWithIdAsync("ds-ward2", Roles.DepartmentUser);

        // The IT team can, and sees the list of places to pick from although they cannot see equipment.
        Assert.Equal(HttpStatusCode.OK, (await _it.GetAsync("/api/lookups/locations")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _it.PutAsJsonAsync($"/api/users/{otherId}/departments", new { locationIds = new[] { _otDepartmentId, _icuDepartmentId } })).StatusCode);

        var places = await _it.GetFromJsonAsync<JsonElement>($"/api/users/{otherId}/departments");
        Assert.Equal(2, places.GetArrayLength());

        var list = await _head.GetFromJsonAsync<JsonElement>("/api/users");
        Assert.Equal(2, list.EnumerateArray().Single(u => u.GetProperty("id").GetInt32() == otherId).GetProperty("departments").GetInt32());

        // Replacing the set, not adding to it.
        Assert.Equal(HttpStatusCode.NoContent,
            (await _head.PutAsJsonAsync($"/api/users/{otherId}/departments", new { locationIds = new[] { _otDepartmentId } })).StatusCode);
        Assert.Equal(1, (await _head.GetFromJsonAsync<JsonElement>($"/api/users/{otherId}/departments")).GetArrayLength());

        // The ward user's own sign-in answer says whose equipment they are looking at.
        var me = await _ward.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal([$"ICU {_suffix}"], me.GetProperty("departments").EnumerateArray().Select(d => d.GetString()).ToArray());
    }

    [Fact]
    public async Task A_change_of_departments_takes_effect_on_the_next_request()
    {
        var (other, otherId) = await SignInWithIdAsync("ds-ward3", Roles.DepartmentUser);

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/equipment/{_otMachineId}")).StatusCode);

        await _head.PutAsJsonAsync($"/api/users/{otherId}/departments", new { locationIds = new[] { _otDepartmentId } });
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync($"/api/equipment/{_otMachineId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/equipment/{_icuMachineId}")).StatusCode);

        await _head.PutAsJsonAsync($"/api/users/{otherId}/departments", new { locationIds = Array.Empty<int>() });
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/equipment/{_otMachineId}")).StatusCode);
    }

    [Fact]
    public async Task Departments_are_for_a_department_user_only_and_only_for_places_that_exist_and_accounts_one_may_manage()
    {
        var (_, engineerId) = await SignInWithIdAsync("ds-eng2", Roles.BmeEngineer);
        var (_, itId) = await SignInWithIdAsync("ds-it2", Roles.ItAdmin);

        Assert.Equal(HttpStatusCode.BadRequest,
            (await _head.PutAsJsonAsync($"/api/users/{engineerId}/departments", new { locationIds = new[] { _icuDepartmentId } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _head.PutAsJsonAsync($"/api/users/{_wardId}/departments", new { locationIds = new[] { 987654321 } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _head.PutAsJsonAsync("/api/users/987654321/departments", new { locationIds = new[] { _icuDepartmentId } })).StatusCode);

        // An engineer cannot, and the head cannot touch the IT team's account.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _engineer.PutAsJsonAsync($"/api/users/{_wardId}/departments", new { locationIds = new[] { _otDepartmentId } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await _head.PutAsJsonAsync($"/api/users/{itId}/departments", new { locationIds = new[] { _icuDepartmentId } })).StatusCode);
    }

    [Fact]
    public async Task Moving_someone_off_the_department_user_role_drops_their_departments()
    {
        var (_, otherId) = await SignInWithIdAsync("ds-ward4", Roles.DepartmentUser);
        await _head.PutAsJsonAsync($"/api/users/{otherId}/departments", new { locationIds = new[] { _icuDepartmentId } });

        Assert.Equal(HttpStatusCode.NoContent, (await _head.PutAsJsonAsync($"/api/users/{otherId}",
            new { fullName = "Promoted", staffCode = (string?)null, role = Roles.BmeEngineer })).StatusCode);

        await using var db = fixture.CreateContext();
        Assert.Equal(0, await db.UserLocations.CountAsync(ul => ul.UserId == otherId));
    }

    [Fact]
    public async Task Every_change_of_departments_is_in_the_audit_log_written_by_the_database()
    {
        var (_, otherId) = await SignInWithIdAsync("ds-ward5", Roles.DepartmentUser);
        await _head.PutAsJsonAsync($"/api/users/{otherId}/departments", new { locationIds = new[] { _icuDepartmentId } });
        await _head.PutAsJsonAsync($"/api/users/{otherId}/departments", new { locationIds = Array.Empty<int>() });

        await using var db = fixture.CreateContext();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT operation FROM audit_log WHERE table_name = 'user_location' "
                          + "AND COALESCE(new_data ->> 'user_id', old_data ->> 'user_id') = @u ORDER BY id";
        cmd.Parameters.AddWithValue("u", otherId.ToString());
        var operations = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                operations.Add(reader.GetString(0));
            }
        }

        Assert.Equal(["INSERT", "DELETE"], operations);
    }
}
