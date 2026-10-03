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

        // The printed report carries the money, so it is not handed to a ward user.
        Assert.Equal(HttpStatusCode.Forbidden, (await _ward.GetAsync($"/api/reports/work-orders/{_icuOrderId}/report.pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _engineer.GetAsync($"/api/reports/work-orders/{_icuOrderId}/report.pdf")).StatusCode);
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
