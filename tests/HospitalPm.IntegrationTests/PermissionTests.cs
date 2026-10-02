using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// What a role may do is one table, not fifty endpoints. These pin the table, what the server tells
/// the screen, and that no route is left open by accident.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PermissionTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "Permission2026!";

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

    [Fact]
    public void Every_permission_has_its_own_name_and_is_listed()
    {
        Assert.Equal(Permissions.All.Count, Permissions.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(Permissions.All, p => Assert.Matches(@"^[a-z-]+\.[a-z-]+$", p));

        // A permission added as a constant but left out of All would be held by no one, silently.
        var declared = typeof(Permissions).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();
        Assert.True(declared.SetEquals(Permissions.All));
    }

    [Fact]
    public void An_admin_holds_every_permission_and_an_employee_holds_none()
    {
        Assert.True(RolePermissions.For(Roles.Admin).SetEquals(Permissions.All));
        Assert.Empty(RolePermissions.For(Roles.Employee));
    }

    [Fact]
    public void A_role_nobody_has_heard_of_may_do_nothing()
    {
        Assert.Empty(RolePermissions.For("Wizard"));
        Assert.Empty(RolePermissions.For((string?)null));
        Assert.False(RolePermissions.Has([], Permissions.EquipmentEdit));
        Assert.False(RolePermissions.Has(["Wizard"], Permissions.EquipmentEdit));
    }

    [Fact]
    public void Someone_with_two_roles_may_do_what_either_may()
    {
        Assert.True(RolePermissions.Has([Roles.Employee, Roles.Admin], Permissions.StaffManage));
        Assert.False(RolePermissions.Has([Roles.Employee], Permissions.StaffManage));
    }

    [Fact]
    public async Task The_server_tells_each_person_what_they_may_do()
    {
        var admin = await SignInAsync("perm-admin", Roles.Admin);
        var employee = await SignInAsync("perm-emp", Roles.Employee);

        var adminMe = await admin.GetFromJsonAsync<JsonElement>("/api/auth/me");
        var granted = adminMe.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToHashSet();
        Assert.True(granted.SetEquals(Permissions.All));

        var employeeMe = await employee.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(0, employeeMe.GetProperty("permissions").GetArrayLength());
    }

    /// <summary>
    /// A new route with no authorization is open to the world. Every /api route must ask for a
    /// signed-in person, and the few that do not are named here, so adding one is a decision
    /// somebody makes on purpose.
    /// </summary>
    [Fact]
    public void Every_api_route_asks_for_a_signed_in_person_except_the_few_named_here()
    {
        var sources = _factory.Services.GetServices<EndpointDataSource>();

        var open = sources
            .SelectMany(s => s.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null
                        || e.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0)
            .Select(e => $"{string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])} {e.RoutePattern.RawText}")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(ExpectedOpenRoutes.Order(StringComparer.Ordinal), open);
    }

    private static readonly string[] ExpectedOpenRoutes =
    [
        // Signing in cannot ask for being signed in.
        "POST /api/auth/login",
        "POST /api/auth/refresh",
        // First-run setup: made before any account exists, and it refuses once there is an administrator.
        "GET /api/setup/status",
        "POST /api/setup/first-admin",
    ];

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
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"{prefix}-{_suffix}", password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }
}
