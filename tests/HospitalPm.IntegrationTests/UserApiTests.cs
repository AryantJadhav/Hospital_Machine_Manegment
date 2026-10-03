using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Managing the hospital's staff.
///
/// Until these endpoints existed an installation had exactly one account
/// forever, which made the roles decorative, every PM signature name the
/// same person, and the work-order screen assign every job to user id 1.
///
/// The interesting cases are the refusals. Creating a user is ordinary; the
/// tests worth having are the ones that stop an administrator locking a
/// hospital out of its own system on a machine with no password-reset path
/// and possibly no network.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class UserApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private int _adminId;
    private string _suffix = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _admin = _factory.CreateClient();
        _suffix = Guid.NewGuid().ToString("N")[..8];

        (_adminId, _) = await NewUserAsync($"ua-admin-{_suffix}", Domain.Identity.Roles.Developer);
        await SignInAsync(_admin, $"ua-admin-{_suffix}");
    }

    private const string Password = "StaffAccounts2026!";

    private async Task<(int Id, Infrastructure.Identity.ApplicationUser User)> NewUserAsync(
        string userName, string role)
    {
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider
            .GetRequiredService<UserManager<Infrastructure.Identity.ApplicationUser>>();

        var user = new Infrastructure.Identity.ApplicationUser
        {
            UserName = userName,
            FullName = userName,
            IsActive = true,
        };

        await users.CreateAsync(user, Password);
        await users.AddToRoleAsync(user, role);
        return (user.Id, user);
    }

    private static async Task SignInAsync(HttpClient client, string userName)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _admin?.Dispose();
        _factory?.Dispose();
    }

    [Fact]
    public async Task An_administrator_can_create_staff_and_they_can_sign_in()
    {
        var response = await _admin.PostAsJsonAsync("/api/users", new
        {
            userName = $"tech-{_suffix}",
            fullName = "R Patil",
            staffCode = "BME-07",
            role = Domain.Identity.Roles.BmeEngineer,
            password = Password,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // The whole point: a second person can now use the system.
        using var technician = _factory.CreateClient();
        await SignInAsync(technician, $"tech-{_suffix}");

        var me = await technician.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("R Patil", me.GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task An_employee_cannot_create_accounts()
    {
        await NewUserAsync($"tech-noadmin-{_suffix}", Domain.Identity.Roles.BmeEngineer);
        using var technician = _factory.CreateClient();
        await SignInAsync(technician, $"tech-noadmin-{_suffix}");

        var response = await technician.PostAsJsonAsync("/api/users", new
        {
            userName = $"sneaky-{_suffix}",
            fullName = "Sneaky",
            staffCode = (string?)null,
            role = Domain.Identity.Roles.Developer,
            password = Password,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A hospital PC has no password-reset path and may not be on a network.
    /// An administrator who deactivates themselves has locked the building out
    /// of its own maintenance records, and nobody on site can undo it.
    /// </summary>
    [Fact]
    public async Task An_administrator_cannot_deactivate_themselves()
    {
        var response = await _admin.PostAsJsonAsync($"/api/users/{_adminId}/deactivate", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("your own account", await response.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same outcome by a different route: deactivating the last remaining
    /// administrator, or demoting them, leaves an installation nobody can
    /// manage.
    /// </summary>
    [Fact]
    public async Task The_last_administrator_cannot_be_removed()
    {
        // A second admin who will do the removing, so "cannot deactivate
        // yourself" is not what is being tested here.
        await NewUserAsync($"ua-admin2-{_suffix}", Domain.Identity.Roles.Developer);
        using var second = _factory.CreateClient();
        await SignInAsync(second, $"ua-admin2-{_suffix}");

        // Take every other admin out of the picture, leaving exactly one.
        await using (var db = fixture.CreateContext())
        {
            var adminRoleId = await db.Roles
                .Where(r => r.Name == Domain.Identity.Roles.Developer)
                .Select(r => r.Id).SingleAsync();

            var adminIds = await db.UserRoles
                .Where(ur => ur.RoleId == adminRoleId)
                .Select(ur => ur.UserId)
                .ToListAsync();

            var keep = await db.Users
                .Where(u => u.UserName == $"ua-admin2-{_suffix}")
                .Select(u => u.Id).SingleAsync();

            await db.Users
                .Where(u => adminIds.Contains(u.Id) && u.Id != keep)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));

            // Demoting the last one is refused...
            var demote = await second.PutAsJsonAsync($"/api/users/{keep}", new
            {
                fullName = "Still Admin",
                staffCode = (string?)null,
                role = Domain.Identity.Roles.BmeEngineer,
            });

            // Refused. Only people who can manage staff can do the demoting, and nobody changes their
            // own role, so the last of them can never be the one removed.
            Assert.Equal(HttpStatusCode.BadRequest, demote.StatusCode);
            Assert.Contains("own role", await demote.Content.ReadAsStringAsync(),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Deactivation has to end their access now, not in fourteen days when the
    /// refresh token would have expired on its own.
    /// </summary>
    [Fact]
    public async Task Deactivating_someone_ends_their_session_immediately()
    {
        await NewUserAsync($"leaver-{_suffix}", Domain.Identity.Roles.BmeEngineer);

        using var leaver = _factory.CreateClient();
        var login = await leaver.PostAsJsonAsync("/api/auth/login",
            new { userName = $"leaver-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        var refreshToken = tokens.GetProperty("refreshToken").GetString();

        var id = await IdOfAsync($"leaver-{_suffix}");
        var deactivated = await _admin.PostAsJsonAsync($"/api/users/{id}/deactivate", new { });
        deactivated.EnsureSuccessStatusCode();

        // Their refresh token is dead, so they cannot mint a new access token.
        var refresh = await leaver.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        // And they cannot sign in again.
        var again = await leaver.PostAsJsonAsync("/api/auth/login",
            new { userName = $"leaver-{_suffix}", password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    [Fact]
    public async Task A_password_reset_ends_every_existing_session()
    {
        await NewUserAsync($"forgot-{_suffix}", Domain.Identity.Roles.Developer);

        using var person = _factory.CreateClient();
        var login = await person.PostAsJsonAsync("/api/auth/login",
            new { userName = $"forgot-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        var refreshToken = tokens.GetProperty("refreshToken").GetString();

        var id = await IdOfAsync($"forgot-{_suffix}");
        var reset = await _admin.PostAsJsonAsync($"/api/users/{id}/reset-password",
            new { password = "BrandNewPassword2026!" });
        reset.EnsureSuccessStatusCode();

        // A password is reset because it may be known to someone else. Leaving
        // the old sessions alive would defeat the point.
        var refresh = await person.PostAsJsonAsync("/api/auth/refresh", new { refreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);

        // The new password works.
        var again = await person.PostAsJsonAsync("/api/auth/login",
            new { userName = $"forgot-{_suffix}", password = "BrandNewPassword2026!" });
        again.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_deactivated_person_is_hidden_from_the_staff_list_but_not_erased()
    {
        await NewUserAsync($"gone-{_suffix}", Domain.Identity.Roles.BmeEngineer);
        var id = await IdOfAsync($"gone-{_suffix}");
        (await _admin.PostAsJsonAsync($"/api/users/{id}/deactivate", new { })).EnsureSuccessStatusCode();

        var active = await _admin.GetFromJsonAsync<JsonElement>("/api/users");
        Assert.DoesNotContain(active.EnumerateArray(),
            u => u.GetProperty("id").GetInt32() == id);

        // Still there, because every PM they signed and every audit row they
        // caused points at them and must stay readable.
        var all = await _admin.GetFromJsonAsync<JsonElement>("/api/users?includeInactive=true");
        var found = all.EnumerateArray().Single(u => u.GetProperty("id").GetInt32() == id);
        Assert.False(found.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_role_is_refused_rather_than_creating_a_powerless_account()
    {
        var response = await _admin.PostAsJsonAsync("/api/users", new
        {
            userName = $"norole-{_suffix}",
            fullName = "No Role",
            staffCode = (string?)null,
            role = "Superuser",
            password = Password,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // An account that exists but can do nothing looks like a broken
        // product rather than a rejected request.
        await using var db = fixture.CreateContext();
        Assert.False(await db.Users.AnyAsync(u => u.UserName == $"norole-{_suffix}"));
    }

    private async Task<int> IdOfAsync(string userName)
    {
        await using var db = fixture.CreateContext();
        return await db.Users.Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync();
    }
}
