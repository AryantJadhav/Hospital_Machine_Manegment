using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>The audit log made readable: who changed what, and when. For the IT team and the Developer.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class AuditLogTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "AuditLog2026!";

    private ApiFactory _factory = null!;
    private string _suffix = null!;
    private HttpClient _it = null!;
    private HttpClient _developer = null!;
    private HttpClient _head = null!;
    private int _headId;
    private string _headUserName = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        _it = (await SignInAsync("au-it", Roles.ItAdmin)).Client;
        _developer = (await SignInAsync("au-dev", Roles.Developer)).Client;
        (_head, _headId) = await SignInAsync("au-head", Roles.BmeHead);
        _headUserName = $"au-head-{_suffix}";
    }

    private async Task<(HttpClient Client, int Id)> SignInAsync(string prefix, string role)
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

    private async Task<int> MakePartAsync(string name)
    {
        var res = await _head.PostAsJsonAsync("/api/spare-parts", new
        {
            partNumber = $"AU-{_suffix}-{Guid.NewGuid():N}"[..24], name, quantityOnHand = 5, unitCost = 100m, unit = "pcs",
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> AuditAsync(HttpClient client, string query = "")
    {
        var res = await client.GetAsync($"/api/admin/audit?pageSize=100{query}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static List<JsonElement> Items(JsonElement audit) => audit.GetProperty("items").EnumerateArray().ToList();

    // ---------------- who may read it ----------------

    [Fact]
    public async Task Only_the_it_team_and_the_developer_can_read_the_audit_log()
    {
        Assert.Equal(HttpStatusCode.OK, (await _it.GetAsync("/api/admin/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _developer.GetAsync("/api/admin/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _it.GetAsync("/api/admin/audit/tables")).StatusCode);

        var (engineer, _) = await SignInAsync("au-eng", Roles.BmeEngineer);
        var (ward, _) = await SignInAsync("au-ward", Roles.DepartmentUser);

        foreach (var client in new[] { _head, engineer, ward })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/audit")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/audit/tables")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/admin/audit")).StatusCode);
    }

    // ---------------- who did it ----------------

    [Fact]
    public async Task A_change_is_recorded_against_the_person_who_made_it()
    {
        var name = $"Filter {_suffix}";
        await MakePartAsync(name);

        var created = Items(await AuditAsync(_it, "&table=spare_part&action=created"))
            .First(i => i.GetProperty("changes").EnumerateArray().Any(c => c.GetProperty("to").GetString() == name));

        Assert.Equal("Created", created.GetProperty("action").GetString());
        Assert.Equal("Spare part", created.GetProperty("tableLabel").GetString());
        Assert.True(created.GetProperty("byRecorded").GetBoolean());
        Assert.Equal(_headUserName, created.GetProperty("by").GetProperty("userName").GetString());
        Assert.Equal(_headId, created.GetProperty("by").GetProperty("id").GetInt32());
        Assert.StartsWith("AU-", created.GetProperty("record").GetString());
    }

    [Fact]
    public async Task Work_by_two_people_in_turn_is_never_put_down_to_the_wrong_one()
    {
        var (other, otherId) = await SignInAsync("au-other", Roles.BmeHead);

        var first = await MakePartAsync($"By head {_suffix}");
        var res = await other.PostAsJsonAsync("/api/spare-parts", new
        {
            partNumber = $"AU2-{_suffix}-{Guid.NewGuid():N}"[..24], name = $"By other {_suffix}", quantityOnHand = 1, unit = "pcs",
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var second = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var third = await MakePartAsync($"By head again {_suffix}");

        var rows = Items(await AuditAsync(_it, "&table=spare_part&action=created"));
        int? Actor(int partId) => rows.First(r => r.GetProperty("recordId").GetString() == partId.ToString())
            .GetProperty("by").GetProperty("id").GetInt32();

        Assert.Equal(_headId, Actor(first));
        Assert.Equal(otherId, Actor(second));
        Assert.Equal(_headId, Actor(third));
    }

    [Fact]
    public async Task A_change_made_with_no_one_signed_in_says_so_rather_than_naming_someone_else()
    {
        var (_, victimId) = await SignInAsync("au-victim", Roles.BmeEngineer);

        // A person signs in as someone else's request has just finished on the same pooled connection:
        // signing in updates the account (last sign-in), with nobody yet signed in to make it.
        await MakePartAsync($"Warm the pool {_suffix}");
        var again = _factory.CreateClient();
        (await again.PostAsJsonAsync("/api/auth/login", new { userName = $"au-victim-{_suffix}", password = Password })).EnsureSuccessStatusCode();

        var rows = Items(await AuditAsync(_it, $"&table=app_user&action=changed&record={victimId}"));
        Assert.NotEmpty(rows);
        Assert.All(rows, r =>
        {
            Assert.False(r.GetProperty("byRecorded").GetBoolean());
            Assert.Equal(JsonValueKind.Null, r.GetProperty("by").ValueKind);
        });

        // And they can be found as a group.
        var none = Items(await AuditAsync(_it, $"&table=app_user&by=none&record={victimId}"));
        Assert.NotEmpty(none);
    }

    [Fact]
    public async Task A_row_written_with_no_person_is_shown_as_not_recorded()
    {
        // Written straight to the database, as a job of the system's own would be: no application behind it.
        var code = $"au-{_suffix}";
        await using (var db = fixture.CreateContext())
        {
            db.EquipmentTypes.Add(new EquipmentType { Code = code, Name = $"System type {_suffix}" });
            await db.SaveChangesAsync();
        }

        var row = Items(await AuditAsync(_it, "&table=equipment_type&action=created"))
            .First(i => i.GetProperty("record").GetString() == $"System type {_suffix}"
                        || i.GetProperty("changes").EnumerateArray().Any(c => c.GetProperty("to").GetString() == code));
        Assert.False(row.GetProperty("byRecorded").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("by").ValueKind);
    }

    // ---------------- what it shows ----------------

    [Fact]
    public async Task A_change_lists_only_what_differs_and_leaves_out_the_noise()
    {
        var id = await MakePartAsync($"Before {_suffix}");
        Assert.True((await _head.PutAsJsonAsync($"/api/spare-parts/{id}", new
        {
            partNumber = (await _head.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{id}")).GetProperty("partNumber").GetString(),
            name = $"After {_suffix}", quantityOnHand = 5, unitCost = 100m, unit = "pcs",
        })).IsSuccessStatusCode);

        var changed = Items(await AuditAsync(_it, $"&table=spare_part&action=changed&record={id}")).First();
        var fields = changed.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("field").GetString()).ToList();

        Assert.Contains("Name", fields);
        Assert.DoesNotContain("Updated at", fields);
        Assert.DoesNotContain("Tenant id", fields);
        Assert.DoesNotContain("Part number", fields);

        var name = changed.GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("field").GetString() == "Name");
        Assert.Equal($"Before {_suffix}", name.GetProperty("from").GetString());
        Assert.Equal($"After {_suffix}", name.GetProperty("to").GetString());
    }

    [Fact]
    public async Task Passwords_and_security_stamps_never_appear_and_cannot_be_searched_for()
    {
        var created = await _developer.PostAsJsonAsync("/api/users", new
        {
            userName = $"au-new-{_suffix}", fullName = "New person", staffCode = (string?)null, role = Roles.BmeEngineer, password = "Fresh2026Ok!",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.NoContent, (await _developer.PostAsJsonAsync($"/api/users/{id}/reset-password",
            new { password = "Another2026!x" })).StatusCode);

        var audit = await AuditAsync(_it, $"&table=app_user&record={id}");
        var text = audit.GetRawText();

        // Not in what is sent...
        Assert.DoesNotContain("password_hash", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("security_stamp", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password hash", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AQAAAA", text);

        // ...but the fact that it changed is.
        var update = Items(audit).First(i => i.GetProperty("action").GetString() == "Changed"
            && i.GetProperty("changes").EnumerateArray().Any(c => c.GetProperty("field").GetString() == "Sign-in details"));
        Assert.Equal("changed", update.GetProperty("changes").EnumerateArray()
            .Single(c => c.GetProperty("field").GetString() == "Sign-in details").GetProperty("to").GetString());

        // ...and not something a search can be used to guess at one letter at a time.
        var guess = await AuditAsync(_it, "&table=app_user&q=AQAAAA");
        Assert.Empty(Items(guess));
    }

    // ---------------- narrowing it ----------------

    [Fact]
    public async Task The_log_can_be_narrowed_by_table_action_person_word_and_period()
    {
        var name = $"Findable {_suffix}";
        var id = await MakePartAsync(name);

        // A word that is in what was written.
        Assert.NotEmpty(Items(await AuditAsync(_it, $"&q={Uri.EscapeDataString(name)}")));
        Assert.Empty(Items(await AuditAsync(_it, $"&q={Uri.EscapeDataString("no-such-word-" + _suffix)}")));

        // By who did it, by table, by action, by the record itself.
        var byHead = Items(await AuditAsync(_it, $"&by={_headId}&table=spare_part"));
        Assert.NotEmpty(byHead);
        Assert.All(byHead, r => Assert.Equal(_headId, r.GetProperty("by").GetProperty("id").GetInt32()));

        Assert.Empty(Items(await AuditAsync(_it, $"&table=spare_part&action=removed&record={id}")));
        Assert.Single(Items(await AuditAsync(_it, $"&table=spare_part&action=created&record={id}")));

        // A period in the past holds none of today; a backwards one, and a made-up action, are refused.
        Assert.Empty(Items(await AuditAsync(_it, "&from=2020-01-01&to=2020-01-31")));
        Assert.Equal(HttpStatusCode.BadRequest, (await _it.GetAsync("/api/admin/audit?from=2026-02-01&to=2026-01-01")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _it.GetAsync("/api/admin/audit?action=exploded")).StatusCode);
    }

    [Fact]
    public async Task The_log_is_paged_newest_first_and_the_total_is_of_what_was_asked_for()
    {
        for (var i = 0; i < 3; i++)
        {
            await MakePartAsync($"Paged {i} {_suffix}");
        }

        var page1 = await _it.GetFromJsonAsync<JsonElement>($"/api/admin/audit?table=spare_part&by={_headId}&pageSize=2&page=1");
        var page2 = await _it.GetFromJsonAsync<JsonElement>($"/api/admin/audit?table=spare_part&by={_headId}&pageSize=2&page=2");

        Assert.Equal(2, page1.GetProperty("items").GetArrayLength());
        Assert.True(page1.GetProperty("total").GetInt32() >= 3);
        Assert.Equal(page1.GetProperty("total").GetInt32(), page2.GetProperty("total").GetInt32());

        var ids1 = Items(page1).Select(i => i.GetProperty("id").GetInt64()).ToList();
        var ids2 = Items(page2).Select(i => i.GetProperty("id").GetInt64()).ToList();
        Assert.True(ids1[0] > ids1[1]);
        Assert.True(ids1[1] > ids2[0]);
    }

    [Fact]
    public async Task The_tables_that_have_been_audited_come_with_names_a_person_would_use()
    {
        await MakePartAsync($"For the list {_suffix}");

        var tables = (await _it.GetFromJsonAsync<JsonElement>("/api/admin/audit/tables")).EnumerateArray().ToList();
        var spare = tables.Single(t => t.GetProperty("table").GetString() == "spare_part");
        Assert.Equal("Spare part", spare.GetProperty("label").GetString());
    }
}
