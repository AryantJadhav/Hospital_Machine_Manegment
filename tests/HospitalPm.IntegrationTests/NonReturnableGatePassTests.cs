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
/// A spare part can be Returnable (it goes back to the vendor when faulty or under warranty), and a gate pass can be
/// non-returnable: the goods leave for good, so the pass is never "out", has no day it is due back, and never comes back.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class NonReturnableGatePassTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "NonReturn2026!";

    private ApiFactory _factory = null!;
    private HttpClient _engineer = null!;
    private HttpClient _head = null!;
    private string _suffix = null!;
    private int _machineId;
    private int _boardId;      // a returnable part
    private int _fuseId;       // a part that is not

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var type = new EquipmentType { Code = $"nr-{_suffix}", Name = $"NR type {_suffix}" };
            var room = new Domain.Locations.Location
            {
                Code = $"NRL-{_suffix}", Name = $"NR room {_suffix}", Level = Domain.Locations.LocationLevel.Room,
            };
            db.EquipmentTypes.Add(type);
            db.Locations.Add(room);
            await db.SaveChangesAsync();

            var machine = new Domain.Assets.Equipment
            {
                AssetTag = $"NRM-{_suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
            };
            db.Equipment.Add(machine);
            await db.SaveChangesAsync();
            _machineId = machine.Id;
        }

        _engineer = await SignInAsync("nr-eng", Roles.BmeEngineer);
        _head = await SignInAsync("nr-head", Roles.BmeHead);

        _boardId = await AddPartAsync("BOARD", returnable: true, spec: "Main board, rev C", make: "Philips", models: "MX450");
        _fuseId = await AddPartAsync("FUSE", returnable: null, spec: "2 A slow-blow");
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
        _engineer?.Dispose();
        _head?.Dispose();
        _factory?.Dispose();
    }

    private async Task<int> AddPartAsync(string number, bool? returnable, string? spec = null, string? make = null, string? models = null)
    {
        var res = await _head.PostAsJsonAsync("/api/spare-parts", new
        {
            partNumber = $"{number}-{_suffix}", name = $"Part {number}", specification = spec, manufacturer = make,
            compatibleModels = models, quantityOnHand = 7, isReturnable = returnable,
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private object NonReturnable(object[]? items = null, string? expected = null) => new
    {
        vendorName = $"Philips service {_suffix}",
        purpose = "Faulty part handed to the vendor",
        isReturnable = false,
        expectedReturnDate = expected,
        items = items ?? [new { sparePartId = _boardId, quantity = 2, remarks = "Dead on arrival" }],
    };

    private async Task<int> WriteAsync(object body)
    {
        var res = await _engineer.PostAsJsonAsync("/api/gate-passes", body);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> GetAsync(int id) => await _engineer.GetFromJsonAsync<JsonElement>($"/api/gate-passes/{id}");

    [Fact]
    public async Task A_spare_part_is_not_returnable_unless_it_is_said_to_be_and_can_be_filtered()
    {
        var board = await _engineer.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{_boardId}");
        var fuse = await _engineer.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{_fuseId}");
        Assert.True(board.GetProperty("isReturnable").GetBoolean());
        Assert.False(fuse.GetProperty("isReturnable").GetBoolean());

        string[] Numbers(JsonElement list) => list.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("partNumber").GetString()!.Replace($"-{_suffix}", string.Empty, StringComparison.Ordinal)).Order().ToArray();

        Assert.Equal(["BOARD"], Numbers(await _engineer.GetFromJsonAsync<JsonElement>($"/api/spare-parts?q={_suffix}&returnable=true")));
        Assert.Equal(["FUSE"], Numbers(await _engineer.GetFromJsonAsync<JsonElement>($"/api/spare-parts?q={_suffix}&returnable=false")));
        Assert.Equal(["BOARD", "FUSE"], Numbers(await _engineer.GetFromJsonAsync<JsonElement>($"/api/spare-parts?q={_suffix}")));

        // An edit that does not mention it keeps it; one that says so changes it.
        Assert.Equal(HttpStatusCode.NoContent, (await _head.PutAsJsonAsync($"/api/spare-parts/{_boardId}", new
        {
            partNumber = $"BOARD-{_suffix}", name = "Part BOARD", quantityOnHand = 7,
        })).StatusCode);
        Assert.True((await _engineer.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{_boardId}")).GetProperty("isReturnable").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await _head.PutAsJsonAsync($"/api/spare-parts/{_boardId}", new
        {
            partNumber = $"BOARD-{_suffix}", name = "Part BOARD", quantityOnHand = 7, isReturnable = false,
        })).StatusCode);
        Assert.False((await _engineer.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{_boardId}")).GetProperty("isReturnable").GetBoolean());
    }

    [Fact]
    public async Task A_non_returnable_pass_is_issued_not_out_and_has_nothing_to_come_back()
    {
        var id = await WriteAsync(NonReturnable());
        var pass = await GetAsync(id);

        Assert.False(pass.GetProperty("isReturnable").GetBoolean());
        Assert.Equal("Issued", pass.GetProperty("status").GetString());
        Assert.False(pass.GetProperty("isOverdue").GetBoolean());
        Assert.Equal(JsonValueKind.Null, pass.GetProperty("expectedReturnDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, pass.GetProperty("returnedOn").ValueKind);

        // It is not on the "out now" list, and so is never counted as overdue; it is on its own.
        var outNow = await _engineer.GetFromJsonAsync<JsonElement>($"/api/gate-passes?status=out&q=Philips%20service%20{_suffix}");
        Assert.Equal(0, outNow.GetProperty("total").GetInt32());
        var issued = await _engineer.GetFromJsonAsync<JsonElement>($"/api/gate-passes?status=issued&q=Philips%20service%20{_suffix}");
        Assert.Equal(1, issued.GetProperty("total").GetInt32());
        var row = issued.GetProperty("items")[0];
        Assert.False(row.GetProperty("isReturnable").GetBoolean());
        Assert.Equal("Issued", row.GetProperty("status").GetString());
        Assert.True(issued.GetProperty("counts").GetProperty("issued").GetInt32() >= 1);
    }

    [Fact]
    public async Task An_ordinary_pass_is_still_returnable_and_out()
    {
        var id = await WriteAsync(new
        {
            vendorName = $"Repairer {_suffix}",
            items = new[] { new { description = "Probe", quantity = 1 } },
        });
        var pass = await GetAsync(id);
        Assert.True(pass.GetProperty("isReturnable").GetBoolean());
        Assert.Equal("Out", pass.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_spare_part_line_fills_in_the_words_and_the_part_number_and_leaves_the_stock_alone()
    {
        var id = await WriteAsync(NonReturnable());
        var line = (await GetAsync(id)).GetProperty("items")[0];

        Assert.Equal(_boardId, line.GetProperty("sparePartId").GetInt32());
        Assert.Equal("Part BOARD, Main board, rev C - Philips MX450", line.GetProperty("description").GetString());
        Assert.Equal($"BOARD-{_suffix}", line.GetProperty("assetCode").GetString());
        Assert.Equal(2, line.GetProperty("quantity").GetInt32());
        Assert.True(line.GetProperty("sparePart").GetProperty("isReturnable").GetBoolean());

        // The pass says what left; the count on the shelf is the department's own to adjust.
        Assert.Equal(7, (await _engineer.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{_boardId}")).GetProperty("quantityOnHand").GetInt32());
    }

    [Fact]
    public async Task A_non_returnable_pass_cannot_have_a_return_date_or_a_machine_from_the_register()
    {
        async Task Refused(object body, string part)
        {
            var res = await _engineer.PostAsJsonAsync("/api/gate-passes", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Contains(part, await res.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }

        // A date that is in range on its own, so it is the kind of pass that refuses it.
        var soon = DateOnly.FromDateTime(DateTime.UtcNow.AddMinutes(330).AddDays(10)).ToString("yyyy-MM-dd");
        await Refused(NonReturnable(expected: soon), "no date of return");
        await Refused(NonReturnable([new { equipmentId = _machineId, quantity = 1 }]), "non-returnable pass is for spare parts");
        await Refused(NonReturnable([new { equipmentId = _machineId, sparePartId = _boardId }]), "non-returnable pass is for spare parts");
    }

    [Fact]
    public async Task A_line_is_a_machine_or_a_part_and_the_part_must_exist()
    {
        var both = await _engineer.PostAsJsonAsync("/api/gate-passes", new
        {
            vendorName = $"Both {_suffix}", items = new[] { new { equipmentId = _machineId, sparePartId = _boardId, quantity = 1 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);

        var unknown = await _engineer.PostAsJsonAsync("/api/gate-passes", NonReturnable([new { sparePartId = 2000000000, quantity = 1 }]));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task A_returnable_pass_can_carry_a_spare_part_and_a_machine_together()
    {
        var id = await WriteAsync(new
        {
            vendorName = $"Mixed {_suffix}",
            items = new object[] { new { equipmentId = _machineId, quantity = 1 }, new { sparePartId = _fuseId, quantity = 3 } },
        });
        var pass = await GetAsync(id);
        Assert.Equal("Out", pass.GetProperty("status").GetString());
        Assert.Equal(2, pass.GetProperty("items").GetArrayLength());

        // Back, and the machine with it.
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/gate-passes/{id}/return", new { })).StatusCode);
    }

    [Fact]
    public async Task A_non_returnable_pass_cannot_be_corrected_or_returned_but_can_be_cancelled_and_keeps_its_number()
    {
        var id = await WriteAsync(NonReturnable());
        var number = (await GetAsync(id)).GetProperty("number").GetInt32();

        var correct = await _engineer.PutAsJsonAsync($"/api/gate-passes/{id}", NonReturnable());
        Assert.Equal(HttpStatusCode.Conflict, correct.StatusCode);
        Assert.Contains("non-returnable", await correct.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        var back = await _engineer.PostAsJsonAsync($"/api/gate-passes/{id}/return", new { });
        Assert.Equal(HttpStatusCode.Conflict, back.StatusCode);
        Assert.Contains("do not come back", await back.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.NoContent,
            (await _engineer.PostAsJsonAsync($"/api/gate-passes/{id}/cancel", new { notes = "Written for the wrong vendor" })).StatusCode);
        var cancelled = await GetAsync(id);
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal(number, cancelled.GetProperty("number").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await _engineer.PostAsJsonAsync($"/api/gate-passes/{id}/cancel", new { })).StatusCode);
    }

    [Fact]
    public async Task A_returnable_pass_cannot_be_turned_into_a_non_returnable_one()
    {
        var id = await WriteAsync(new { vendorName = $"Keep {_suffix}", items = new[] { new { description = "Cable", quantity = 1 } } });
        var res = await _engineer.PutAsJsonAsync($"/api/gate-passes/{id}", new
        {
            vendorName = $"Keep {_suffix}", isReturnable = false, items = new[] { new { description = "Cable", quantity = 1 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.True((await GetAsync(id)).GetProperty("isReturnable").GetBoolean());
    }

    [Fact]
    public async Task The_printed_pass_says_it_is_non_returnable()
    {
        var id = await WriteAsync(NonReturnable());
        var res = await _engineer.GetAsync($"/api/gate-passes/{id}/pdf");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
        Assert.True((await res.Content.ReadAsByteArrayAsync()).Length > 1000);
    }

    [Fact]
    public async Task The_database_will_not_hold_a_non_returnable_pass_with_a_return_date_or_a_returnable_one_as_issued()
    {
        var id = await WriteAsync(NonReturnable());
        var returnable = await WriteAsync(new { vendorName = $"Plain {_suffix}", items = new[] { new { description = "Lead", quantity = 1 } } });

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();

        foreach (var (sql, key) in new[]
        {
            ("UPDATE gate_pass SET expected_return_date = pass_date WHERE id = @id", id),
            ("UPDATE gate_pass SET status = 40 WHERE id = @id", returnable),
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", key);
            var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal("23514", ex.SqlState);
        }
    }
}
