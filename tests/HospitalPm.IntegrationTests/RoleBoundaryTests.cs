using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The whole line between the two roles, in one place.
///
/// The permission split is spread across nine endpoint files, which is how a
/// route quietly ends up on the wrong side of it — the kind of mistake nobody
/// notices until a technician is refused their own work list, or until an
/// Employee turns out to be able to condemn a machine.
///
/// These tests deliberately use ids that do not exist. Authorization runs
/// before the handler, so a refused route answers 403 and a permitted one
/// answers 404 or 400. That distinction is exactly what is being asserted,
/// and it means the file needs no fixture data and cannot be made flaky by
/// another test's rows in the shared database.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class RoleBoundaryTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "RoleBoundary2026!";
    private const int Missing = 2_000_000_000;

    private ApiFactory _factory = null!;
    private HttpClient _employee = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        _employee = await SignedInAsync($"rb-emp-{suffix}", Roles.Employee);
        _admin = await SignedInAsync($"rb-adm-{suffix}", Roles.Admin);
    }

    private async Task<HttpClient> SignedInAsync(string userName, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<UserManager<Infrastructure.Identity.ApplicationUser>>();

            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = userName, FullName = userName, IsActive = true,
            };

            var created = await users.CreateAsync(user, Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));

            var assigned = await users.AddToRoleAsync(user, role);
            Assert.True(assigned.Succeeded, string.Join("; ", assigned.Errors.Select(e => e.Description)));
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
        _employee?.Dispose();
        _admin?.Dispose();
        _factory?.Dispose();
    }

    /// <summary>
    /// Every route an Employee must not reach.
    ///
    /// Two things are asserted per route, not one: that the Employee is
    /// refused, and that the same request from an Admin is not. Without the
    /// second half a typo in a route would pass this test by 404-ing both
    /// callers, and the file would prove nothing.
    /// </summary>
    public static TheoryData<string, string> AdminOnly() => new()
    {
        // Deciding what gets done, and to whom.
        { "POST", $"/api/pm/tasks/{Missing}/skip" },
        { "POST", $"/api/work-orders/{Missing}/assign" },

        // Committing the department to a schedule.
        { "POST", "/api/pm/schedules" },
        { "POST", "/api/pm/schedules/bulk" },
        { "PUT", $"/api/pm/schedules/{Missing}" },

        // A report is evidence, and one that should never have been uploaded has to be removable.
        { "DELETE", $"/api/pm/attachments/{Missing}" },
        { "POST", "/api/pm/generate" },

        // The register itself.
        { "POST", "/api/equipment" },
        { "PUT", $"/api/equipment/{Missing}" },
        { "POST", $"/api/equipment/{Missing}/condemn" },
        { "POST", "/api/locations" },
        { "PUT", $"/api/locations/{Missing}" },
        { "DELETE", $"/api/locations/{Missing}" },

        // What the department calls a kind of machine.
        { "GET", "/api/equipment-types" },
        { "POST", "/api/equipment-types" },
        { "PUT", $"/api/equipment-types/{Missing}" },

        // What the checklists say, which is what a PM means.
        { "POST", "/api/checklists" },
        { "PUT", $"/api/checklists/{Missing}/draft" },
        { "POST", $"/api/checklists/{Missing}/publish" },

        // Bulk work: imports and label sheets.
        { "GET", "/api/equipment/import/template" },
        { "POST", "/api/labels/sheet" },
        { "POST", "/api/labels/zpl" },

        // Running the installation.
        { "GET", "/api/users" },
        { "POST", "/api/users" },
        { "GET", "/api/admin/backups" },
        { "POST", "/api/admin/backups/run" },
        { "POST", $"/api/admin/backups/{Missing}/restore" },
        { "GET", "/api/admin/diagnostics" },
        { "GET", "/api/admin/licence" },
        { "GET", "/api/admin/pilot-metrics" },

        // Ends with an executable running as LocalSystem. The signature is
        // the real gate - an Admin cannot install an unsigned update either -
        // but there is no reason for anyone else to reach the route.
        { "GET", "/api/admin/update" },
        { "POST", "/api/admin/update/check" },
        { "POST", "/api/admin/update/install" },
    };

    [Theory]
    [MemberData(nameof(AdminOnly))]
    public async Task An_employee_is_refused(string method, string route)
    {
        var refused = await SendAsync(_employee, method, route);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        // The route exists and the refusal was about the role, not the URL.
        var allowed = await SendAsync(_admin, method, route);
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed.StatusCode);
    }

    /// <summary>
    /// Every route an Employee must reach. An account that cannot do these is
    /// not worth issuing: this is the entire job.
    /// </summary>
    public static TheoryData<string, string> Shared() => new()
    {
        // The work list, and doing the work on it.
        { "GET", "/api/pm/tasks" },
        { "GET", "/api/pm/summary" },
        { "GET", "/api/pm/reminders" },

        // A PM the maintenance contract vendor does: recording it, and its report.
        { "GET", $"/api/pm/tasks/{Missing}/vendor" },
        { "POST", $"/api/pm/tasks/{Missing}/complete-by-vendor" },
        { "POST", $"/api/pm/tasks/{Missing}/attachments" },
        { "GET", $"/api/pm/attachments/{Missing}" },
        { "GET", $"/api/pm/tasks/{Missing}/form" },
        { "POST", $"/api/pm/tasks/{Missing}/complete" },

        // Faults: reporting one, and seeing it through.
        { "GET", "/api/work-orders" },
        { "POST", "/api/work-orders" },
        { "POST", $"/api/work-orders/{Missing}/notes" },
        { "POST", $"/api/work-orders/{Missing}/resolve" },

        // Reading the register, including from a scanned label.
        { "GET", "/api/equipment" },
        { "GET", "/api/equipment/by-tag/nothing-here" },
        { "GET", $"/api/equipment/{Missing}/history" },
        { "GET", "/api/locations" },
        { "GET", "/api/checklists" },
        { "GET", "/api/lookups/equipment-types" },

        // One QR for the machine in front of you, which is not a label sheet.
        { "GET", "/api/labels/qr/nothing-here" },

        // The screen the shift starts on.
        { "GET", "/api/dashboard" },
    };

    [Theory]
    [MemberData(nameof(Shared))]
    public async Task An_employee_is_allowed(string method, string route)
    {
        var response = await SendAsync(_employee, method, route);

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Nothing at all without a token. Worth one case rather than repeating it
    /// per route: the fallback policy is set once in Program.cs, so it either
    /// holds everywhere or nowhere.
    /// </summary>
    [Fact]
    public async Task A_stranger_is_refused_the_work_list()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/pm/tasks");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string route)
    {
        // An empty JSON body, not an empty request: a bad body is a 400, which
        // is on the permitted side of the only distinction being drawn here,
        // whereas no body at all can be a 415 before authorization has run.
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "POST" or "PUT")
        {
            request.Content = JsonContent.Create(new { });
        }

        return client.SendAsync(request);
    }
}
