using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Giving one person a section their role does not, or taking one away: the rules, and that the
/// server enforces them on the very next request.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class AccessGrantTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "AccessGrant2026!";
    private const string StockReport = "/api/reports/stock";

    private ApiFactory _factory = null!;
    private string _suffix = null!;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _factory?.Dispose();

    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    // ---------------- the sum, with no database ----------------

    private static PermissionGrant Row(string permission, string effect, DateOnly? expires = null) =>
        new() { UserId = 1, Permission = permission, Effect = effect, ExpiresOn = expires, GrantedByUserId = 1 };

    [Fact]
    public void A_given_section_is_added_to_the_role_and_a_taken_one_is_removed()
    {
        var set = EffectivePermissions.For(
            [Roles.BmeEngineer],
            [Row(Permissions.ReportsView, GrantEffect.Grant), Row(Permissions.PmWork, GrantEffect.Revoke)],
            Today);

        Assert.Contains(Permissions.ReportsView, set);
        Assert.DoesNotContain(Permissions.PmWork, set);
        // Everything else the role holds is still held.
        Assert.Contains(Permissions.RegisterView, set);
    }

    [Fact]
    public void A_grant_lasts_through_its_last_day_and_then_stops()
    {
        var onTheLastDay = EffectivePermissions.For([Roles.BmeEngineer], [Row(Permissions.ReportsView, GrantEffect.Grant, Today)], Today);
        var theNextDay = EffectivePermissions.For([Roles.BmeEngineer], [Row(Permissions.ReportsView, GrantEffect.Grant, Today.AddDays(-1))], Today);

        Assert.Contains(Permissions.ReportsView, onTheLastDay);
        Assert.DoesNotContain(Permissions.ReportsView, theNextDay);
    }

    [Fact]
    public void A_taking_away_that_has_run_out_gives_the_section_back()
    {
        var set = EffectivePermissions.For(
            [Roles.BmeEngineer], [Row(Permissions.PmWork, GrantEffect.Revoke, Today.AddDays(-1))], Today);

        Assert.Contains(Permissions.PmWork, set);
    }

    [Fact]
    public void A_developer_holds_everything_whatever_is_written()
    {
        var set = EffectivePermissions.For([Roles.Developer], [Row(Permissions.ReportsView, GrantEffect.Revoke)], Today);

        Assert.True(set.SetEquals(Permissions.All));
    }

    [Fact]
    public void Looking_after_staff_and_giving_access_cannot_be_given_or_taken_away()
    {
        Assert.False(PermissionCatalog.IsGrantable(Permissions.StaffManage));
        Assert.False(PermissionCatalog.IsGrantable(Permissions.AccessManage));

        var set = EffectivePermissions.For(
            [Roles.BmeEngineer],
            [Row(Permissions.StaffManage, GrantEffect.Grant), Row(Permissions.AccessManage, GrantEffect.Grant)],
            Today);
        Assert.DoesNotContain(Permissions.StaffManage, set);
        Assert.DoesNotContain(Permissions.AccessManage, set);

        var head = EffectivePermissions.For([Roles.BmeHead], [Row(Permissions.StaffManage, GrantEffect.Revoke)], Today);
        Assert.Contains(Permissions.StaffManage, head);
    }

    [Fact]
    public void Every_section_that_can_be_given_is_a_real_permission_with_a_name_and_a_group()
    {
        Assert.All(PermissionCatalog.All, p =>
        {
            Assert.Contains(p.Permission, Permissions.All);
            Assert.False(string.IsNullOrWhiteSpace(p.Label));
            Assert.False(string.IsNullOrWhiteSpace(p.Group));
        });

        // Each is listed once, and everything but the two that cannot be given is listed.
        Assert.Equal(PermissionCatalog.All.Count, PermissionCatalog.All.Select(p => p.Permission).Distinct().Count());
        var left = Permissions.All.Except(PermissionCatalog.All.Select(p => p.Permission)).Order().ToArray();
        // Looking after staff, giving access, and the one that makes a person a department user.
        Assert.Equal([Permissions.AccessManage, Permissions.DepartmentView, Permissions.StaffManage], left);
    }

    // ---------------- through the API ----------------

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
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"{prefix}-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (client, id);
    }

    private static Task<HttpResponseMessage> SetAsync(HttpClient developer, int userId, params object[] grants) =>
        developer.PutAsJsonAsync($"/api/access/users/{userId}", new { grants });

    private static object Give(string permission, DateOnly? expires = null, string? note = null) =>
        new { permission, effect = "Grant", expiresOn = expires?.ToString("yyyy-MM-dd"), note };

    private static object TakeAway(string permission) => new { permission, effect = "Revoke" };

    [Fact]
    public async Task A_section_given_to_an_engineer_works_on_their_very_next_request_and_stops_when_taken_back()
    {
        var (developer, _) = await SignInAsync("ag-dev", Roles.Developer);
        var (engineer, engineerId) = await SignInAsync("ag-eng", Roles.BmeEngineer);

        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.GetAsync(StockReport)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SetAsync(developer, engineerId, Give(Permissions.ReportsView, note: "Q3 review"))).StatusCode);

        // The same token, no new sign-in.
        Assert.Equal(HttpStatusCode.OK, (await engineer.GetAsync(StockReport)).StatusCode);

        var me = await engineer.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Contains(Permissions.ReportsView,
            me.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));

        Assert.Equal(HttpStatusCode.OK, (await SetAsync(developer, engineerId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.GetAsync(StockReport)).StatusCode);
    }

    [Fact]
    public async Task A_section_taken_from_the_head_of_biomedical_is_gone_for_them()
    {
        var (developer, _) = await SignInAsync("ag-dev2", Roles.Developer);
        var (head, headId) = await SignInAsync("ag-head", Roles.BmeHead);

        Assert.Equal(HttpStatusCode.OK, (await head.GetAsync(StockReport)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await SetAsync(developer, headId, TakeAway(Permissions.ReportsView))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await head.GetAsync(StockReport)).StatusCode);

        // And the rest of what the head holds is untouched.
        Assert.Equal(HttpStatusCode.OK, (await head.GetAsync("/api/equipment")).StatusCode);
    }

    [Fact]
    public async Task A_grant_that_has_run_out_gives_nothing()
    {
        var (_, engineerId) = await SignInAsync("ag-eng3", Roles.BmeEngineer);
        var engineer = await ReSignInAsync($"ag-eng3-{_suffix}");

        // Written straight into the table: the API will not take a date in the past.
        await using (var db = fixture.CreateContext())
        {
            db.PermissionGrants.Add(new PermissionGrant
            {
                UserId = engineerId, Permission = Permissions.ReportsView, Effect = GrantEffect.Grant,
                ExpiresOn = Today.AddDays(-1), GrantedByUserId = engineerId,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.GetAsync(StockReport)).StatusCode);
    }

    private async Task<HttpClient> ReSignInAsync(string userName)
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    [Fact]
    public async Task Only_the_developer_can_give_access_and_nobody_else_can_even_look_at_the_page()
    {
        var (_, engineerId) = await SignInAsync("ag-eng4", Roles.BmeEngineer);
        var (head, _) = await SignInAsync("ag-head4", Roles.BmeHead);
        var (it, _) = await SignInAsync("ag-it4", Roles.ItAdmin);
        var (engineer, _) = await SignInAsync("ag-eng4b", Roles.BmeEngineer);

        foreach (var client in new[] { head, it, engineer })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await SetAsync(client, engineerId, Give(Permissions.ReportsView))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/access/users/{engineerId}")).StatusCode);
        }
    }

    [Fact]
    public async Task The_hospital_can_read_what_a_person_has_been_given_but_an_engineer_cannot()
    {
        var (developer, _) = await SignInAsync("ag-dev5", Roles.Developer);
        var (_, engineerId) = await SignInAsync("ag-eng5", Roles.BmeEngineer);
        var (head, _) = await SignInAsync("ag-head5", Roles.BmeHead);
        var (it, _) = await SignInAsync("ag-it5", Roles.ItAdmin);
        var (other, _) = await SignInAsync("ag-eng5b", Roles.BmeEngineer);

        await SetAsync(developer, engineerId, Give(Permissions.ReportsView, Today.AddDays(30), "Asked by Dr Rao"), TakeAway(Permissions.PmWork));

        foreach (var client in new[] { head, it })
        {
            var view = await client.GetFromJsonAsync<JsonElement>($"/api/users/{engineerId}/access");
            var grants = view.GetProperty("grants").EnumerateArray().ToList();
            Assert.Equal(2, grants.Count);

            var given = grants.Single(g => g.GetProperty("permission").GetString() == Permissions.ReportsView);
            Assert.Equal("Grant", given.GetProperty("effect").GetString());
            Assert.Equal("Asked by Dr Rao", given.GetProperty("note").GetString());
            Assert.StartsWith("ag-dev5", given.GetProperty("grantedByName").GetString());
            Assert.False(given.GetProperty("expired").GetBoolean());
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await other.GetAsync($"/api/users/{engineerId}/access")).StatusCode);

        // And the Staff list says how many, so a person with extras is not mistaken for a plain role.
        var list = await head.GetFromJsonAsync<JsonElement>("/api/users");
        var row = list.EnumerateArray().Single(u => u.GetProperty("id").GetInt32() == engineerId);
        Assert.Equal(1, row.GetProperty("accessGiven").GetInt32());
        Assert.Equal(1, row.GetProperty("accessTakenAway").GetInt32());
    }

    [Fact]
    public async Task Access_is_refused_for_a_developer_a_department_user_and_a_user_who_does_not_exist()
    {
        var (developer, _) = await SignInAsync("ag-dev6", Roles.Developer);
        var (_, otherDeveloperId) = await SignInAsync("ag-dev6b", Roles.Developer);
        var (_, departmentId) = await SignInAsync("ag-dept6", Roles.DepartmentUser);

        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(developer, otherDeveloperId, Give(Permissions.ReportsView))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(developer, departmentId, Give(Permissions.RegisterView))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SetAsync(developer, 987654321, Give(Permissions.ReportsView))).StatusCode);

        var view = await developer.GetFromJsonAsync<JsonElement>($"/api/access/users/{departmentId}");
        Assert.False(string.IsNullOrWhiteSpace(view.GetProperty("locked").GetString()));
    }

    [Fact]
    public async Task What_cannot_be_given_what_is_already_so_and_what_makes_no_sense_is_refused()
    {
        var (developer, _) = await SignInAsync("ag-dev7", Roles.Developer);
        var (_, engineerId) = await SignInAsync("ag-eng7", Roles.BmeEngineer);

        // Looking after staff, and giving access.
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(developer, engineerId, Give(Permissions.StaffManage))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(developer, engineerId, Give(Permissions.AccessManage))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(developer, engineerId, Give("nonsense.permission"))).StatusCode);

        // Giving what the role has, or taking what it lacks.
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(developer, engineerId, Give(Permissions.RegisterView))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SetAsync(developer, engineerId, TakeAway(Permissions.ReportsView))).StatusCode);

        // The same section twice, an unknown effect, a date in the past or far off, a very long note.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SetAsync(developer, engineerId, Give(Permissions.ReportsView), Give(Permissions.ReportsView))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SetAsync(developer, engineerId, new { permission = Permissions.ReportsView, effect = "Lend" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SetAsync(developer, engineerId, Give(Permissions.ReportsView, Today.AddDays(-1)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SetAsync(developer, engineerId, Give(Permissions.ReportsView, Today.AddYears(20)))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await SetAsync(developer, engineerId, Give(Permissions.ReportsView, note: new string('x', 501)))).StatusCode);

        // Nothing was written by any of them.
        var view = await developer.GetFromJsonAsync<JsonElement>($"/api/access/users/{engineerId}");
        Assert.Equal(0, view.GetProperty("grants").GetArrayLength());
    }

    [Fact]
    public async Task Saving_the_same_set_again_keeps_who_gave_it_and_when_and_changing_it_updates_that_row()
    {
        var (developer, _) = await SignInAsync("ag-dev8", Roles.Developer);
        var (_, engineerId) = await SignInAsync("ag-eng8", Roles.BmeEngineer);

        await SetAsync(developer, engineerId, Give(Permissions.ReportsView, note: "first"));
        DateTime firstAt;
        await using (var db = fixture.CreateContext())
        {
            firstAt = (await db.PermissionGrants.SingleAsync(g => g.UserId == engineerId)).GrantedAtUtc;
        }

        await Task.Delay(50);
        await SetAsync(developer, engineerId, Give(Permissions.ReportsView, note: "first"));
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(firstAt, (await db.PermissionGrants.SingleAsync(g => g.UserId == engineerId)).GrantedAtUtc);
        }

        await SetAsync(developer, engineerId, Give(Permissions.ReportsView, note: "second"));
        await using (var db = fixture.CreateContext())
        {
            var row = await db.PermissionGrants.SingleAsync(g => g.UserId == engineerId);
            Assert.Equal("second", row.Note);
            Assert.Equal(1, await db.PermissionGrants.CountAsync(g => g.UserId == engineerId));
        }
    }

    [Fact]
    public async Task Every_change_is_in_the_audit_log_written_by_the_database_and_the_table_refuses_nonsense()
    {
        var (developer, _) = await SignInAsync("ag-dev9", Roles.Developer);
        var (_, engineerId) = await SignInAsync("ag-eng9", Roles.BmeEngineer);

        await SetAsync(developer, engineerId, Give(Permissions.ReportsView));
        await SetAsync(developer, engineerId);

        await using var db = fixture.CreateContext();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT operation FROM audit_log WHERE table_name = 'user_permission' "
                          + "AND COALESCE(new_data ->> 'user_id', old_data ->> 'user_id') = @u ORDER BY id";
        cmd.Parameters.AddWithValue("u", engineerId.ToString());
        var operations = new List<string>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                operations.Add(reader.GetString(0));
            }
        }

        Assert.Equal(["INSERT", "DELETE"], operations);

        // The database holds the rule too, for anything that writes another way.
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO user_permission (user_id, permission, effect, granted_by_user_id) "
            + $"VALUES ({engineerId}, 'reports.view', 'Lend', {engineerId})"));
    }

    [Fact]
    public async Task Picking_a_person_needs_a_section_that_picks_people_and_gives_names_and_nothing_more()
    {
        var (developer, _) = await SignInAsync("ag-dev11", Roles.Developer);
        var (engineer, engineerId) = await SignInAsync("ag-eng11", Roles.BmeEngineer);
        var (it, _) = await SignInAsync("ag-it11", Roles.ItAdmin);

        // An engineer cannot list people, and the full staff list is not theirs either way.
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.GetAsync("/api/people")).StatusCode);

        // Given "assign work" they can pick whom to assign to, without being given the staff list.
        await SetAsync(developer, engineerId, Give(Permissions.WorkOrdersAssign));
        var people = await engineer.GetFromJsonAsync<JsonElement>("/api/people");
        var first = people.EnumerateArray().First();
        Assert.Equal(["fullName", "id"], first.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.GetAsync("/api/users")).StatusCode);

        // The IT team looks after the staff, so it can see them.
        Assert.Equal(HttpStatusCode.OK, (await it.GetAsync("/api/people")).StatusCode);
    }

    [Fact]
    public async Task The_catalog_is_open_to_anyone_signed_in()
    {
        var (engineer, _) = await SignInAsync("ag-eng10", Roles.BmeEngineer);

        var catalog = await engineer.GetFromJsonAsync<JsonElement>("/api/access/catalog");

        Assert.Equal(PermissionCatalog.All.Count, catalog.GetArrayLength());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/access/catalog")).StatusCode);
    }
}
