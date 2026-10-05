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
    public void A_developer_holds_every_permission()
    {
        Assert.True(RolePermissions.For(Roles.Developer).SetEquals(Permissions.All));
    }

    [Fact]
    public void The_head_of_biomedical_decides_for_the_department_and_does_not_run_the_installation()
    {
        var head = RolePermissions.For(Roles.BmeHead);

        // Everything the department decides, including the money, the reports and exporting its data.
        string[] decides =
        [
            Permissions.EquipmentEdit, Permissions.EquipmentTypesEdit, Permissions.LocationsEdit, Permissions.LabelsPrint,
            Permissions.DataImport, Permissions.DataExport, Permissions.ChecklistsEdit, Permissions.PmManage,
            Permissions.WorkOrdersAssign, Permissions.WorkOrdersCancel, Permissions.AttachmentsDelete,
            Permissions.SparePartsEdit, Permissions.TrainingEdit, Permissions.ReportsView, Permissions.StaffManage,
        ];
        Assert.All(decides, p => Assert.Contains(p, head));

        // And everything an engineer does.
        Assert.All(RolePermissions.For(Roles.BmeEngineer), p => Assert.Contains(p, head));

        // Not how the installation is run, and not giving other people access.
        string[] notTheirs =
        [
            Permissions.SystemBackups, Permissions.SystemRestore, Permissions.SystemUpdates,
            Permissions.SystemDiagnostics, Permissions.SystemLicence, Permissions.AuditView, Permissions.AccessManage,
        ];
        Assert.All(notTheirs, p => Assert.DoesNotContain(p, head));
    }

    [Fact]
    public void Giving_access_is_held_by_the_developer_alone()
    {
        foreach (var role in Roles.All.Where(r => r != Roles.Developer))
        {
            Assert.DoesNotContain(Permissions.AccessManage, RolePermissions.For(role));
        }
    }

    [Fact]
    public void The_it_team_may_stop_the_developer_account_and_nobody_else_may()
    {
        Assert.Contains(Roles.Developer, Roles.PausableBy([Roles.ItAdmin]));

        // Everything they manage they may stop, and nothing more.
        Assert.Equal([Roles.BmeEngineer, Roles.DepartmentUser], Roles.PausableBy([Roles.BmeHead]));
        Assert.Empty(Roles.PausableBy([Roles.BmeEngineer]));
        Assert.Equal(Roles.All, Roles.PausableBy([Roles.Developer]));

        // But the IT team still cannot create, change, or reset a Developer.
        Assert.DoesNotContain(Roles.Developer, Roles.ManageableBy(Roles.ItAdmin));
    }

    [Fact]
    public void An_engineer_holds_what_an_employee_always_did_and_nothing_that_decides_for_the_department()
    {
        var engineer = RolePermissions.For(Roles.BmeEngineer);

        string[] floor =
        [
            Permissions.RegisterView, Permissions.SparePartsView, Permissions.ChecklistsView, Permissions.TrainingView,
            Permissions.PmWork, Permissions.WorkOrdersView, Permissions.WorkOrdersReport, Permissions.WorkOrdersNote,
            Permissions.WorkOrdersWork, Permissions.EquipmentMove, Permissions.GatePassView, Permissions.GatePassEdit,
            Permissions.IncidentsView, Permissions.IncidentsManage,
        ];
        Assert.True(engineer.SetEquals(floor));

        // Incidents are written up by the department they happen in. The biomedical team reads and closes them.
        Assert.DoesNotContain(Permissions.IncidentsReport, engineer);
        Assert.DoesNotContain(Permissions.IncidentsReport, RolePermissions.For(Roles.BmeHead));
        Assert.DoesNotContain(Permissions.IncidentsReport, RolePermissions.For(Roles.ItAdmin));

        // Nothing that edits the register, commits the department, reads the money, or runs the installation.
        Assert.DoesNotContain(Permissions.EquipmentEdit, engineer);
        Assert.DoesNotContain(Permissions.PmManage, engineer);
        Assert.DoesNotContain(Permissions.WorkOrdersAssign, engineer);
        Assert.DoesNotContain(Permissions.ReportsView, engineer);
        Assert.DoesNotContain(Permissions.StaffManage, engineer);
        Assert.DoesNotContain(Permissions.SystemBackups, engineer);
    }

    [Fact]
    public void The_it_team_runs_the_installation_and_cannot_see_the_equipment()
    {
        var it = RolePermissions.For(Roles.ItAdmin);

        Assert.True(it.SetEquals(
        [
            Permissions.StaffManage, Permissions.SystemUpdates, Permissions.SystemDiagnostics,
            Permissions.SystemLicence, Permissions.AuditView,
        ]));
        Assert.DoesNotContain(Permissions.RegisterView, it);
        Assert.DoesNotContain(Permissions.WorkOrdersView, it);
    }

    [Fact]
    public void Backups_and_restore_are_the_developers_alone()
    {
        string[] only = [Permissions.SystemBackups, Permissions.SystemRestore];

        foreach (var role in Roles.All.Where(r => r != Roles.Developer))
        {
            Assert.All(Permissions.DeveloperOnly, p => Assert.DoesNotContain(p, RolePermissions.For(role)));
        }

        Assert.All(only, p => Assert.Contains(p, RolePermissions.For(Roles.Developer)));

        // Not even by name: nothing on the Access page can hand them out, and a grant written anyway does nothing.
        Assert.All(only, p => Assert.False(PermissionCatalog.IsGrantable(p)));
        var set = EffectivePermissions.For(
            [Roles.ItAdmin],
            [new PermissionGrant { Permission = Permissions.SystemBackups, Effect = GrantEffect.Grant }],
            DateOnly.FromDateTime(DateTime.UtcNow));
        Assert.DoesNotContain(Permissions.SystemBackups, set);
    }

    [Fact]
    public void A_department_user_may_see_their_own_departments_report_a_fault_and_answer_about_it_and_nothing_more()
    {
        var ward = RolePermissions.For(Roles.DepartmentUser);

        Assert.True(ward.SetEquals(
        [
            Permissions.DepartmentView, Permissions.WorkOrdersView, Permissions.WorkOrdersReport, Permissions.WorkOrdersNote,
            Permissions.IncidentsView, Permissions.IncidentsReport,
        ]));

        // They write incidents up; looking into them and closing them is the biomedical team's.
        Assert.DoesNotContain(Permissions.IncidentsManage, ward);

        // The whole register is what limits them to their own part of it: they must not hold it.
        Assert.DoesNotContain(Permissions.RegisterView, ward);
        Assert.DoesNotContain(Permissions.WorkOrdersWork, ward);
        Assert.DoesNotContain(Permissions.SparePartsView, ward);
        Assert.DoesNotContain(Permissions.ReportsView, ward);
    }

    [Fact]
    public void No_role_that_works_across_the_whole_hospital_is_limited_to_departments()
    {
        // Being limited to departments means holding DepartmentView and not RegisterView; only the ward role does.
        foreach (var role in new[] { Roles.Developer, Roles.BmeHead, Roles.BmeEngineer })
        {
            Assert.Contains(Permissions.RegisterView, RolePermissions.For(role));
        }

        Assert.DoesNotContain(Permissions.DepartmentView, RolePermissions.For(Roles.ItAdmin));
    }

    [Fact]
    public void Nobody_may_manage_an_account_of_a_kind_above_their_own()
    {
        Assert.Equal(Roles.All, Roles.ManageableBy(Roles.Developer));
        Assert.DoesNotContain(Roles.Developer, Roles.ManageableBy(Roles.ItAdmin));
        Assert.Contains(Roles.BmeHead, Roles.ManageableBy(Roles.ItAdmin));

        // The head of department looks after the department's own people, not the hospital's IT team.
        Assert.Equal([Roles.BmeEngineer, Roles.DepartmentUser], Roles.ManageableBy(Roles.BmeHead));
        Assert.Empty(Roles.ManageableBy(Roles.BmeEngineer));
        Assert.Empty(Roles.ManageableBy(Roles.DepartmentUser));
        Assert.Empty(Roles.ManageableBy("Wizard"));
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
        Assert.True(RolePermissions.Has([Roles.BmeEngineer, Roles.ItAdmin], Permissions.StaffManage));
        Assert.False(RolePermissions.Has([Roles.BmeEngineer], Permissions.StaffManage));
    }

    [Fact]
    public async Task The_server_tells_each_person_what_they_may_do()
    {
        var head = await SignInAsync("perm-head", Roles.BmeHead);
        var engineer = await SignInAsync("perm-eng", Roles.BmeEngineer);
        var department = await SignInAsync("perm-dept", Roles.DepartmentUser);

        var headMe = await head.GetFromJsonAsync<JsonElement>("/api/auth/me");
        var granted = headMe.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToHashSet();
        Assert.True(granted.SetEquals(RolePermissions.For(Roles.BmeHead)));
        Assert.Equal(["BmeEngineer", "DepartmentUser"],
            headMe.GetProperty("pausableRoles").EnumerateArray().Select(r => r.GetString()).ToArray());
        Assert.Equal(["BmeEngineer", "DepartmentUser"],
            headMe.GetProperty("manageableRoles").EnumerateArray().Select(r => r.GetString()).ToArray());

        var engineerMe = await engineer.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(RolePermissions.For(Roles.BmeEngineer).Count, engineerMe.GetProperty("permissions").GetArrayLength());
        Assert.Equal(0, engineerMe.GetProperty("manageableRoles").GetArrayLength());

        var departmentMe = await department.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(RolePermissions.For(Roles.DepartmentUser).Count, departmentMe.GetProperty("permissions").GetArrayLength());
        Assert.Equal(0, departmentMe.GetProperty("departments").GetArrayLength());
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
        // A backup download. A browser cannot send the sign-in on a link, so the link carries a one-use pass the
        // signed-in Developer asked for, good for one file for one minute (DownloadTickets).
        "GET /api/admin/backups/download/{token}",
        // The lock screen has nobody signed in. One says whether the installation is locked and for whom; the other
        // does nothing unless the code was signed by us for this licence and is newer than the last one.
        "GET /api/licence/lock",
        "POST /api/licence/code",
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
