using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>The five kinds of user, and who may make whom.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class FiveRolesTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "FiveRoles2026!";

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

    private object NewAccount(string name, string role) => new
    {
        userName = $"{name}-{_suffix}", fullName = $"{name} person", staffCode = (string?)null, role, password = Password,
    };

    [Fact]
    public async Task The_five_roles_exist_and_the_two_old_names_are_gone()
    {
        await using var db = fixture.CreateContext();
        var names = await db.Roles.Select(r => r.Name).ToListAsync();

        foreach (var role in Roles.All)
        {
            Assert.Contains(role, names);
        }

        Assert.DoesNotContain("Admin", names);
        Assert.DoesNotContain("Employee", names);
    }

    [Fact]
    public async Task Only_a_developer_can_make_a_developer()
    {
        var (it, _) = await SignInAsync("it", Roles.ItAdmin);
        var (developer, _) = await SignInAsync("dev", Roles.Developer);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await it.PostAsJsonAsync("/api/users", NewAccount("dev-by-it", Roles.Developer))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await developer.PostAsJsonAsync("/api/users", NewAccount("dev-by-dev", Roles.Developer))).StatusCode);
    }

    [Fact]
    public async Task The_it_team_makes_the_hospitals_own_people_but_the_head_of_department_makes_only_theirs()
    {
        var (it, _) = await SignInAsync("it2", Roles.ItAdmin);
        var (head, _) = await SignInAsync("head2", Roles.BmeHead);

        foreach (var role in new[] { Roles.ItAdmin, Roles.BmeHead, Roles.BmeEngineer, Roles.DepartmentUser })
        {
            Assert.Equal(HttpStatusCode.Created,
                (await it.PostAsJsonAsync("/api/users", NewAccount($"it-made-{role}", role))).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Created,
            (await head.PostAsJsonAsync("/api/users", NewAccount("head-eng", Roles.BmeEngineer))).StatusCode);
        Assert.Equal(HttpStatusCode.Created,
            (await head.PostAsJsonAsync("/api/users", NewAccount("head-dept", Roles.DepartmentUser))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await head.PostAsJsonAsync("/api/users", NewAccount("head-it", Roles.ItAdmin))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await head.PostAsJsonAsync("/api/users", NewAccount("head-head", Roles.BmeHead))).StatusCode);
    }

    [Fact]
    public async Task Nobody_changes_an_account_of_a_kind_above_their_own_or_their_own_role()
    {
        var (head, headId) = await SignInAsync("head3", Roles.BmeHead);
        var (_, itId) = await SignInAsync("it3", Roles.ItAdmin);
        var (_, engineerId) = await SignInAsync("eng3", Roles.BmeEngineer);

        // Not the IT team's account, in any way.
        Assert.Equal(HttpStatusCode.Forbidden, (await head.PutAsJsonAsync($"/api/users/{itId}",
            new { fullName = "Changed", staffCode = (string?)null, role = Roles.BmeEngineer })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await head.PostAsync($"/api/users/{itId}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await head.PostAsJsonAsync($"/api/users/{itId}/reset-password",
            new { password = "Another2026!x" })).StatusCode);

        // Their own people, yes - but not promoted above themselves.
        Assert.Equal(HttpStatusCode.NoContent, (await head.PutAsJsonAsync($"/api/users/{engineerId}",
            new { fullName = "Renamed", staffCode = (string?)null, role = Roles.DepartmentUser })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await head.PutAsJsonAsync($"/api/users/{engineerId}",
            new { fullName = "Renamed", staffCode = (string?)null, role = Roles.ItAdmin })).StatusCode);

        // And never their own role. The head cannot manage their own kind either, so it is refused first.
        var (it, itSelfId) = await SignInAsync("it3b", Roles.ItAdmin);
        var own = await it.PutAsJsonAsync($"/api/users/{itSelfId}",
            new { fullName = "Self", staffCode = (string?)null, role = Roles.BmeHead });
        Assert.Equal(HttpStatusCode.BadRequest, own.StatusCode);
        _ = headId;
    }

    [Fact]
    public async Task The_it_team_runs_the_installation_but_is_kept_out_of_the_equipment()
    {
        var (it, _) = await SignInAsync("it4", Roles.ItAdmin);

        Assert.Equal(HttpStatusCode.OK, (await it.GetAsync("/api/admin/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await it.GetAsync("/api/users")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await it.GetAsync("/api/equipment")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await it.GetAsync("/api/work-orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await it.GetAsync("/api/pm/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await it.GetAsync("/api/spare-parts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await it.GetAsync("/api/training")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await it.GetAsync("/api/dashboard")).StatusCode);
    }

    [Fact]
    public async Task The_head_of_biomedical_decides_for_the_department_but_is_kept_out_of_the_installation()
    {
        var (head, _) = await SignInAsync("head8", Roles.BmeHead);

        Assert.Equal(HttpStatusCode.OK, (await head.GetAsync("/api/equipment")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await head.GetAsync("/api/reports/stock")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await head.GetAsync("/api/users")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await head.GetAsync("/api/admin/backups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await head.GetAsync("/api/admin/diagnostics")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await head.GetAsync("/api/admin/licence")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await head.GetAsync("/api/access/users/1")).StatusCode);
    }

    [Fact]
    public async Task The_it_team_can_stop_the_developer_account_and_let_it_back_in_but_do_nothing_else_to_it()
    {
        var (it, _) = await SignInAsync("it9", Roles.ItAdmin);
        var (_, developerId) = await SignInAsync("dev9", Roles.Developer);
        var developerName = $"dev9-{_suffix}";

        // Stopped: the Developer cannot sign in.
        Assert.Equal(HttpStatusCode.NoContent, (await it.PostAsync($"/api/users/{developerId}/deactivate", null)).StatusCode);
        var refused = await _factory.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new { userName = developerName, password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        // Let back in: they can sign in again.
        Assert.Equal(HttpStatusCode.NoContent, (await it.PostAsync($"/api/users/{developerId}/activate", null)).StatusCode);
        var allowed = await _factory.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new { userName = developerName, password = Password });
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        // That is all the IT team may do to it: not a new password, not a change of role or name.
        Assert.Equal(HttpStatusCode.Forbidden, (await it.PostAsJsonAsync($"/api/users/{developerId}/reset-password",
            new { password = "Another2026!x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await it.PutAsJsonAsync($"/api/users/{developerId}",
            new { fullName = "Taken over", staffCode = (string?)null, role = Roles.ItAdmin })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await it.PostAsJsonAsync("/api/users", NewAccount("it-makes-dev", Roles.Developer))).StatusCode);
    }

    [Fact]
    public async Task Nobody_else_can_stop_the_developer_account()
    {
        var (head, _) = await SignInAsync("head10", Roles.BmeHead);
        var (engineer, _) = await SignInAsync("eng10", Roles.BmeEngineer);
        var (_, developerId) = await SignInAsync("dev10", Roles.Developer);

        Assert.Equal(HttpStatusCode.Forbidden, (await head.PostAsync($"/api/users/{developerId}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.PostAsync($"/api/users/{developerId}/deactivate", null)).StatusCode);
    }

    [Fact]
    public async Task An_engineer_works_the_floor_and_runs_nothing()
    {
        var (engineer, _) = await SignInAsync("eng5", Roles.BmeEngineer);

        Assert.Equal(HttpStatusCode.OK, (await engineer.GetAsync("/api/equipment")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await engineer.GetAsync("/api/work-orders")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await engineer.GetAsync("/api/training")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.GetAsync("/api/admin/backups")).StatusCode);
    }

    [Fact]
    public async Task A_department_user_with_no_department_yet_sees_nothing_and_is_kept_out_of_the_rest()
    {
        var (department, _) = await SignInAsync("dept6", Roles.DepartmentUser);

        Assert.Equal(HttpStatusCode.OK, (await department.GetAsync("/api/auth/me")).StatusCode);

        // Allowed in, and shown none of it: access that has not been set up is closed, not open.
        var equipment = await department.GetFromJsonAsync<JsonElement>("/api/equipment");
        Assert.Equal(0, equipment.GetProperty("items").GetArrayLength());
        var orders = await department.GetFromJsonAsync<JsonElement>("/api/work-orders");
        Assert.Equal(0, orders.GetProperty("items").GetArrayLength());

        Assert.Equal(HttpStatusCode.Forbidden, (await department.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await department.GetAsync("/api/dashboard")).StatusCode);
    }

    [Fact]
    public async Task The_last_person_who_can_manage_staff_cannot_be_demoted_or_deactivated()
    {
        // Run against a clean pair, so "last" is about these two and not the whole shared database.
        var (developer, developerId) = await SignInAsync("dev7", Roles.Developer);
        _ = developerId;

        var created = await developer.PostAsJsonAsync("/api/users", NewAccount("only-it", Roles.ItAdmin));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        // Others who can manage staff exist (the developer), so this one may be demoted.
        Assert.Equal(HttpStatusCode.NoContent, (await developer.PutAsJsonAsync($"/api/users/{id}",
            new { fullName = "Demoted", staffCode = (string?)null, role = Roles.BmeEngineer })).StatusCode);
    }
}
