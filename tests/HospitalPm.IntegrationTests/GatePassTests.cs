using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Returnable gate passes: a machine that cannot be repaired where it stands goes to the vendor on one.
/// Engineers write them and everyone who works on the equipment reads them; they are never deleted.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class GatePassTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "GatePass2026!";

    private ApiFactory _factory = null!;
    private HttpClient _engineer = null!;
    private HttpClient _head = null!;
    private HttpClient _it = null!;
    private HttpClient _ward = null!;
    private int _engineerId;
    private string _suffix = null!;
    private int _firstId;
    private int _secondId;
    private int _thirdId;
    private string _firstTag = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var type = new EquipmentType { Code = $"gp-{_suffix}", Name = $"Gate pass type {_suffix}" };
            db.EquipmentTypes.Add(type);
            var room = new Domain.Locations.Location
            {
                Code = $"GPL-{_suffix}", Name = $"Gate pass room {_suffix}", Level = Domain.Locations.LocationLevel.Room,
            };
            db.Locations.Add(room);
            await db.SaveChangesAsync();

            _firstTag = $"GPM-{_suffix}-1".ToUpperInvariant();
            var first = new Domain.Assets.Equipment
            {
                AssetTag = _firstTag, EquipmentTypeId = type.Id, LocationId = room.Id,
                Manufacturer = $"Acme {_suffix}", Model = "V-300", SerialNumber = $"SN-{_suffix}-1",
            };
            var second = new Domain.Assets.Equipment
            {
                AssetTag = $"GPM-{_suffix}-2".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
            };
            var third = new Domain.Assets.Equipment
            {
                AssetTag = $"GPM-{_suffix}-3".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
            };
            db.Equipment.AddRange(first, second, third);
            await db.SaveChangesAsync();
            _firstId = first.Id;
            _secondId = second.Id;
            _thirdId = third.Id;
        }

        (_engineer, _engineerId) = await SignInAsync("gp-eng", Roles.BmeEngineer);
        (_head, _) = await SignInAsync("gp-head", Roles.BmeHead);
        (_it, _) = await SignInAsync("gp-it", Roles.ItAdmin);
        (_ward, _) = await SignInAsync("gp-ward", Roles.DepartmentUser);
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
        _engineer?.Dispose();
        _head?.Dispose();
        _it?.Dispose();
        _ward?.Dispose();
        _factory?.Dispose();
    }

    private static string Day(int daysFromToday) =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(daysFromToday)).ToString("yyyy-MM-dd");

    private string Vendor => $"Bet Medical {_suffix}";

    private object Pass(
        object[]? items = null,
        string? vendor = null,
        string? passDate = null,
        string? expected = null,
        int? workOrderId = null,
        string? purpose = null) => new
    {
        passDate,
        vendorName = vendor ?? Vendor,
        contactPerson = "Rupesh",
        contactPhone = "9356253653",
        purpose,
        workOrderId,
        expectedReturnDate = expected,
        authorisedBy = (string?)null,
        notes = (string?)null,
        items = items ?? [new { description = "Meniscus positioning device", assetCode = (string?)null, quantity = 6, remarks = "Sending to the company for repair" }],
    };

    private async Task<(int Id, int Number)> WriteAsync(object body, HttpClient? as_ = null)
    {
        var res = await (as_ ?? _engineer).PostAsJsonAsync("/api/gate-passes", body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("id").GetInt32(), json.GetProperty("number").GetInt32());
    }

    private async Task<JsonElement> GetAsync(int id)
    {
        var res = await _engineer.GetAsync($"/api/gate-passes/{id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> ListAsync(string query)
    {
        var res = await _engineer.GetAsync($"/api/gate-passes?{query}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<string> ErrorOf(HttpResponseMessage res) =>
        (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    private async Task<int> RaiseWorkOrderAsync(int machineId)
    {
        var res = await _engineer.PostAsJsonAsync("/api/work-orders",
            new { equipmentId = machineId, faultDescription = $"Will not power on {_suffix}", priority = 20 });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    // ---------------------------------------------------------------- who may

    [Fact]
    public async Task Engineers_and_the_head_write_passes_and_read_them_but_the_it_team_and_a_ward_do_neither()
    {
        var (id, _) = await WriteAsync(Pass());
        var (headId, _) = await WriteAsync(Pass(), _head);

        Assert.Equal(HttpStatusCode.OK, (await _head.GetAsync($"/api/gate-passes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _engineer.GetAsync($"/api/gate-passes/{headId}")).StatusCode);

        foreach (var outsider in new[] { _it, _ward })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync("/api/gate-passes")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/gate-passes/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/gate-passes/{id}/pdf")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync("/api/gate-passes", Pass())).StatusCode);
        }
    }

    [Fact]
    public async Task Someone_who_can_only_look_cannot_write()
    {
        // Granted the view alone, the way the Access page does it: a person added to the list for reading.
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
        var viewer = new Infrastructure.Identity.ApplicationUser { UserName = $"gp-view-{_suffix}", FullName = "Viewer", IsActive = true };
        Assert.True((await users.CreateAsync(viewer, Password)).Succeeded);
        await users.AddToRoleAsync(viewer, Roles.DepartmentUser);

        await using var db = fixture.CreateContext();
        db.PermissionGrants.Add(new Domain.Identity.PermissionGrant
        {
            UserId = viewer.Id, Permission = Permissions.GatePassView, Effect = GrantEffect.Grant,
            GrantedByUserId = viewer.Id,
        });
        await db.SaveChangesAsync();

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = viewer.UserName, password = Password });
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/gate-passes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/gate-passes", Pass())).StatusCode);
    }

    // ---------------------------------------------------------------- writing one

    [Fact]
    public async Task A_pass_is_numbered_from_the_book_and_each_number_is_new()
    {
        var (_, first) = await WriteAsync(Pass());
        var (_, second) = await WriteAsync(Pass());

        Assert.True(first >= 1001, $"The book starts at 1001, got {first}.");
        Assert.True(second > first);
    }

    [Fact]
    public async Task A_pass_for_a_machine_fills_in_its_words_and_number_from_the_register()
    {
        var (id, number) = await WriteAsync(Pass(items: [new { equipmentId = _firstId }]));

        var pass = await GetAsync(id);
        Assert.Equal($"GP-{number}", pass.GetProperty("reference").GetString());
        Assert.Equal("Out", pass.GetProperty("status").GetString());
        // Said nothing about the purpose or the day: the form's own wording and today.
        Assert.Equal("Sending to the company for repair", pass.GetProperty("purpose").GetString());
        Assert.Equal(Day(0), pass.GetProperty("passDate").GetString());

        var line = pass.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(_firstTag, line.GetProperty("assetCode").GetString());
        Assert.Equal(1, line.GetProperty("quantity").GetInt32());
        var words = line.GetProperty("description").GetString()!;
        Assert.Contains($"Gate pass type {_suffix}", words);
        Assert.Contains($"Acme {_suffix} V-300", words);
        Assert.Contains($"SN-{_suffix}-1", words);
        Assert.Equal(_firstId, line.GetProperty("machine").GetProperty("id").GetInt32());

        Assert.Equal("gp-eng person", pass.GetProperty("createdByName").GetString());
    }

    [Fact]
    public async Task A_line_that_is_not_a_registered_machine_goes_out_as_written()
    {
        var (id, _) = await WriteAsync(Pass(items:
        [
            new { description = "SpO2 probe, adult", assetCode = (string?)null, quantity = 4, remarks = "Cable damaged" },
            new { equipmentId = _firstId },
        ]));

        var pass = await GetAsync(id);
        var lines = pass.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("SpO2 probe, adult", lines[0].GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, lines[0].GetProperty("assetCode").ValueKind);
        Assert.Equal(4, lines[0].GetProperty("quantity").GetInt32());
        Assert.Equal(JsonValueKind.Null, lines[0].GetProperty("machine").ValueKind);
        Assert.Equal(5, pass.GetProperty("totalQuantity").GetInt32());
    }

    [Fact]
    public async Task A_pass_can_be_tied_to_the_service_request_it_is_for()
    {
        var workOrderId = await RaiseWorkOrderAsync(_firstId);
        var (id, _) = await WriteAsync(Pass(items: [new { equipmentId = _firstId }], workOrderId: workOrderId));

        var pass = await GetAsync(id);
        Assert.Equal(workOrderId, pass.GetProperty("workOrder").GetProperty("id").GetInt32());
        Assert.StartsWith("WO-", pass.GetProperty("workOrder").GetProperty("number").GetString());

        var found = await ListAsync($"workOrderId={workOrderId}");
        Assert.Equal(id, found.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task A_pass_that_makes_no_sense_is_refused_with_the_reason()
    {
        async Task Refused(object body, string part)
        {
            var res = await _engineer.PostAsJsonAsync("/api/gate-passes", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Contains(part, await ErrorOf(res), StringComparison.OrdinalIgnoreCase);
        }

        await Refused(Pass(vendor: "  "), "company or person");
        await Refused(Pass(items: []), "at least one item");
        await Refused(Pass(items: [new { description = "  ", quantity = 1 }]), "description");
        await Refused(Pass(items: [new { description = "Probe", quantity = 0 }]), "quantity");
        await Refused(Pass(items: [new { equipmentId = _firstId, quantity = 3 }]), "one machine");
        await Refused(Pass(items: [new { equipmentId = 2_000_000_000 }]), "not on the register");
        await Refused(Pass(items: [new { equipmentId = _firstId }, new { equipmentId = _firstId }]), "listed twice");
        await Refused(Pass(passDate: Day(3)), "not come yet");
        await Refused(Pass(passDate: Day(-2), expected: Day(-5)), "expected back");
        await Refused(Pass(workOrderId: 2_000_000_000), "service request");
    }

    // ---------------------------------------------------------------- one place at a time

    [Fact]
    public async Task A_machine_that_is_already_out_cannot_be_sent_out_again_until_it_is_back()
    {
        var (firstId, firstNumber) = await WriteAsync(Pass(items: [new { equipmentId = _firstId }]));

        var again = await _engineer.PostAsJsonAsync("/api/gate-passes", Pass(items: [new { equipmentId = _firstId }]));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        var why = await ErrorOf(again);
        Assert.Contains(_firstTag, why);
        Assert.Contains($"GP-{firstNumber}", why);

        // Back from the vendor: free to go out again.
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/gate-passes/{firstId}/return", new { })).StatusCode);
        await WriteAsync(Pass(items: [new { equipmentId = _firstId }]));
    }

    [Fact]
    public async Task A_cancelled_pass_does_not_keep_its_machine_from_being_sent()
    {
        var (id, _) = await WriteAsync(Pass(items: [new { equipmentId = _firstId }]));
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/gate-passes/{id}/cancel", new { notes = "Vendor collected it another day" })).StatusCode);

        await WriteAsync(Pass(items: [new { equipmentId = _firstId }]));
    }

    // ---------------------------------------------------------------- changing one

    [Fact]
    public async Task An_open_pass_can_be_corrected_and_keeps_its_number()
    {
        var (id, number) = await WriteAsync(Pass(items: [new { equipmentId = _firstId }]));

        var edit = await _engineer.PutAsJsonAsync($"/api/gate-passes/{id}", Pass(
            vendor: $"Other Vendor {_suffix}",
            items: [new { equipmentId = _firstId }, new { equipmentId = _secondId }],
            expected: Day(20)));
        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);

        var pass = await GetAsync(id);
        Assert.Equal(number, pass.GetProperty("number").GetInt32());
        Assert.Equal($"Other Vendor {_suffix}", pass.GetProperty("vendorName").GetString());
        Assert.Equal(2, pass.GetProperty("items").GetArrayLength());
        Assert.Equal(Day(20), pass.GetProperty("expectedReturnDate").GetString());
    }

    [Fact]
    public async Task Correcting_a_pass_does_not_trip_over_its_own_machine()
    {
        var (id, _) = await WriteAsync(Pass(items: [new { equipmentId = _firstId }]));

        // The machine is out on this very pass, so it is not "already out" for the purpose of saving it again.
        Assert.Equal(HttpStatusCode.NoContent,
            (await _engineer.PutAsJsonAsync($"/api/gate-passes/{id}", Pass(items: [new { equipmentId = _firstId }]))).StatusCode);
    }

    [Fact]
    public async Task A_pass_cannot_be_corrected_to_include_a_machine_that_is_out_on_another()
    {
        await WriteAsync(Pass(items: [new { equipmentId = _firstId }]));
        var (id, _) = await WriteAsync(Pass(items: [new { equipmentId = _secondId }]));

        var res = await _engineer.PutAsJsonAsync($"/api/gate-passes/{id}", Pass(items: [new { equipmentId = _secondId }, new { equipmentId = _firstId }]));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("already out", await ErrorOf(res));
    }

    // ---------------------------------------------------------------- coming back

    [Fact]
    public async Task Recording_that_it_came_back_closes_the_pass_with_the_day_and_how_it_went()
    {
        var (id, _) = await WriteAsync(Pass(passDate: Day(-10), expected: Day(-2), items: [new { equipmentId = _firstId }]));

        var back = await _engineer.PostAsJsonAsync($"/api/gate-passes/{id}/return", new { returnedOn = Day(-1), notes = "Repaired, tested OK" });
        Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);

        var pass = await GetAsync(id);
        Assert.Equal("Returned", pass.GetProperty("status").GetString());
        Assert.Equal(Day(-1), pass.GetProperty("returnedOn").GetString());
        Assert.Equal("Repaired, tested OK", pass.GetProperty("outcomeNotes").GetString());
        Assert.Equal(9, pass.GetProperty("daysOut").GetInt32());
        // It was late in coming back, but it is back: not overdue any more.
        Assert.False(pass.GetProperty("isOverdue").GetBoolean());
    }

    [Fact]
    public async Task The_day_it_came_back_must_be_a_possible_day()
    {
        var (id, _) = await WriteAsync(Pass(passDate: Day(-5)));

        var future = await _engineer.PostAsJsonAsync($"/api/gate-passes/{id}/return", new { returnedOn = Day(2) });
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);

        var before = await _engineer.PostAsJsonAsync($"/api/gate-passes/{id}/return", new { returnedOn = Day(-9) });
        Assert.Equal(HttpStatusCode.BadRequest, before.StatusCode);
        Assert.Contains("before the pass was written", await ErrorOf(before));

        // Neither attempt closed it.
        Assert.Equal("Out", (await GetAsync(id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_pass_that_is_back_or_cancelled_is_final()
    {
        var (backId, _) = await WriteAsync(Pass());
        var (cancelledId, _) = await WriteAsync(Pass());
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/gate-passes/{backId}/return", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/gate-passes/{cancelledId}/cancel", new { notes = "Written twice" })).StatusCode);

        foreach (var id in new[] { backId, cancelledId })
        {
            Assert.Equal(HttpStatusCode.Conflict, (await _engineer.PostAsJsonAsync($"/api/gate-passes/{id}/return", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await _engineer.PostAsJsonAsync($"/api/gate-passes/{id}/cancel", new { })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await _engineer.PutAsJsonAsync($"/api/gate-passes/{id}", Pass())).StatusCode);
        }

        var cancelled = await GetAsync(cancelledId);
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal("Written twice", cancelled.GetProperty("outcomeNotes").GetString());
    }

    [Fact]
    public async Task What_went_out_cannot_be_changed_afterwards_even_by_the_database_directly()
    {
        var (id, _) = await WriteAsync(Pass());
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/gate-passes/{id}/return", new { })).StatusCode);

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();

        foreach (var sql in new[]
        {
            "UPDATE gate_pass_item SET quantity = 99 WHERE gate_pass_id = @id",
            "DELETE FROM gate_pass_item WHERE gate_pass_id = @id",
            "INSERT INTO gate_pass_item (gate_pass_id, description, quantity) VALUES (@id, 'Slipped in later', 1)",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", id);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal("23514", ex.SqlState);
        }

        Assert.Equal(1, (await GetAsync(id)).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task The_database_will_not_hold_a_returned_pass_with_no_day_it_came_back()
    {
        var (id, _) = await WriteAsync(Pass());

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE gate_pass SET status = 20 WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("23514", ex.SqlState);
    }

    [Fact]
    public async Task A_cancelled_number_is_not_handed_out_again()
    {
        var (id, cancelledNumber) = await WriteAsync(Pass());
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/gate-passes/{id}/cancel", new { })).StatusCode);

        var (_, next) = await WriteAsync(Pass());
        Assert.True(next > cancelledNumber);

        // And it is still on the list, as cancelled.
        var all = await ListAsync($"q={cancelledNumber}&status=cancelled");
        Assert.Equal(id, all.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task There_is_no_way_to_delete_a_pass()
    {
        var (id, _) = await WriteAsync(Pass());

        var res = await _head.DeleteAsync($"/api/gate-passes/{id}");
        Assert.True(res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
        Assert.Equal(HttpStatusCode.OK, (await _head.GetAsync($"/api/gate-passes/{id}")).StatusCode);
    }

    // ---------------------------------------------------------------- the list

    [Fact]
    public async Task What_is_late_is_called_late_and_what_is_due_today_is_not()
    {
        var (lateId, _) = await WriteAsync(Pass(passDate: Day(-10), expected: Day(-1)));
        var (todayId, _) = await WriteAsync(Pass(passDate: Day(-3), expected: Day(0)));
        var (laterId, _) = await WriteAsync(Pass(passDate: Day(-1), expected: Day(14)));
        var (noDateId, _) = await WriteAsync(Pass());

        Assert.True((await GetAsync(lateId)).GetProperty("isOverdue").GetBoolean());
        Assert.False((await GetAsync(todayId)).GetProperty("isOverdue").GetBoolean());
        Assert.False((await GetAsync(laterId)).GetProperty("isOverdue").GetBoolean());
        Assert.False((await GetAsync(noDateId)).GetProperty("isOverdue").GetBoolean());

        var overdue = await ListAsync($"q={_suffix}&status=overdue");
        Assert.Equal(lateId, overdue.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt32());

        var counts = overdue.GetProperty("counts");
        Assert.Equal(4, counts.GetProperty("out").GetInt32());
        Assert.Equal(1, counts.GetProperty("overdue").GetInt32());
    }

    [Fact]
    public async Task The_list_puts_what_is_still_out_first_and_the_latest_due_back_soonest()
    {
        var (backId, _) = await WriteAsync(Pass(passDate: Day(-8)));
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/gate-passes/{backId}/return", new { })).StatusCode);

        var (soonId, _) = await WriteAsync(Pass(passDate: Day(-5), expected: Day(2)));
        var (lateId, _) = await WriteAsync(Pass(passDate: Day(-9), expected: Day(-4)));

        var ids = (await ListAsync($"q={_suffix}")).GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").GetInt32()).ToList();

        // Overdue before due-soon, both before the one that is back.
        Assert.Equal([lateId, soonId, backId], ids);
    }

    [Fact]
    public async Task A_pass_is_found_by_its_number_its_company_or_what_is_on_it()
    {
        var (id, number) = await WriteAsync(Pass(items: [new { equipmentId = _firstId }, new { description = $"Charger {_suffix}", quantity = 2 }]));
        await WriteAsync(Pass(vendor: $"Somebody Else {_suffix}"));

        async Task<List<int>> Ids(string q) => (await ListAsync($"q={Uri.EscapeDataString(q)}")).GetProperty("items")
            .EnumerateArray().Select(i => i.GetProperty("id").GetInt32()).ToList();

        Assert.Equal([id], await Ids($"GP-{number}"));
        Assert.Equal([id], await Ids($"gp{number}"));
        Assert.Equal([id], await Ids(number.ToString()));
        Assert.Contains(id, await Ids($"Bet Medical {_suffix}"));
        Assert.Equal([id], await Ids($"Charger {_suffix}"));
        Assert.Equal([id], await Ids(_firstTag));
        Assert.Empty(await Ids($"nothing-like-this-{_suffix}"));

        var byMachine = await ListAsync($"equipmentId={_firstId}");
        Assert.Equal(id, byMachine.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task The_list_says_how_much_is_on_each_pass_and_pages()
    {
        for (var i = 0; i < 3; i++)
        {
            await WriteAsync(Pass(items: [new { description = "Probe A", quantity = 2 }, new { description = "Probe B", quantity = 3 }]));
        }

        var page1 = await ListAsync($"q={_suffix}&pageSize=2");
        Assert.Equal(3, page1.GetProperty("total").GetInt32());
        Assert.Equal(2, page1.GetProperty("items").GetArrayLength());

        var row = page1.GetProperty("items")[0];
        Assert.Equal(2, row.GetProperty("itemCount").GetInt32());
        Assert.Equal(5, row.GetProperty("totalQuantity").GetInt32());
        Assert.Equal(["Probe A", "Probe B"], row.GetProperty("someItems").EnumerateArray().Select(s => s.GetString()).ToArray());

        Assert.Equal(1, (await ListAsync($"q={_suffix}&pageSize=2&page=2")).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task An_unknown_status_filter_is_refused()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _engineer.GetAsync("/api/gate-passes?status=lost")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_pass_is_not_found()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _engineer.GetAsync("/api/gate-passes/2000000000")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _engineer.GetAsync("/api/gate-passes/2000000000/pdf")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _engineer.PostAsJsonAsync("/api/gate-passes/2000000000/return", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _engineer.PostAsJsonAsync("/api/gate-passes/2000000000/cancel", new { })).StatusCode);
    }

    // ---------------------------------------------------------------- the paper

    [Fact]
    public async Task The_printed_pass_is_a_pdf_for_each_of_the_three_copies()
    {
        var workOrderId = await RaiseWorkOrderAsync(_firstId);
        var (id, number) = await WriteAsync(Pass(workOrderId: workOrderId, expected: Day(30),
            items: [new { equipmentId = _firstId }, new { description = "Charger", quantity = 2 }]));

        var res = await _engineer.GetAsync($"/api/gate-passes/{id}/pdf");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"GP-{number}.pdf", res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName?.Trim('"'));

        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));

        // One page for each copy: vendor, security, the department.
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page\b").Count);
    }

    [Fact]
    public async Task A_returned_and_a_cancelled_pass_still_print()
    {
        var (backId, _) = await WriteAsync(Pass());
        var (cancelledId, _) = await WriteAsync(Pass());
        await _engineer.PostAsJsonAsync($"/api/gate-passes/{backId}/return", new { });
        await _engineer.PostAsJsonAsync($"/api/gate-passes/{cancelledId}/cancel", new { });

        foreach (var id in new[] { backId, cancelledId })
        {
            var res = await _engineer.GetAsync($"/api/gate-passes/{id}/pdf");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
    }

    // ---------------------------------------------------------------- the record

    [Fact]
    public async Task The_database_writes_the_audit_trail_and_it_says_who_wrote_the_pass()
    {
        var (id, _) = await WriteAsync(Pass());
        await _engineer.PostAsJsonAsync($"/api/gate-passes/{id}/return", new { notes = "Repaired" });

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT operation, changed_by FROM audit_log WHERE table_name = 'gate_pass' AND record_pk = @id ORDER BY id", conn);
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
        Assert.All(rows, r => Assert.Equal(_engineerId.ToString(System.Globalization.CultureInfo.InvariantCulture), r.By));
    }
}
