namespace HospitalPm.Domain.Identity;

/// <summary>
/// Which permissions each role holds. The one place that says so.
///
/// Reading this table is reading the access policy. It is a table in code, not a screen, so that a
/// hospital cannot misconfigure it and an installation takes five minutes to set up.
/// </summary>
public static class RolePermissions
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> Everything =
        new HashSet<string>(Permissions.All, StringComparer.Ordinal);

    /// <summary>The day-to-day work of an engineer: PM rounds, faults, and reading the register.</summary>
    private static readonly IReadOnlySet<string> Floor = new HashSet<string>(StringComparer.Ordinal)
    {
        Permissions.RegisterView,
        Permissions.SparePartsView,
        Permissions.ChecklistsView,
        Permissions.TrainingView,
        Permissions.PmWork,
        Permissions.WorkOrdersView,
        Permissions.WorkOrdersReport,
        Permissions.WorkOrdersNote,
        Permissions.WorkOrdersWork,
        Permissions.EquipmentMove,
        Permissions.GatePassView,
        Permissions.GatePassEdit,
        Permissions.IncidentsView,
        Permissions.IncidentsReport,
        Permissions.IncidentsManage,
    };

    /// <summary>
    /// A person in another department: the equipment of their own departments, the service requests on
    /// it, reporting a fault, and answering about it. No costs, no spare parts, no other departments.
    /// Holding <see cref="Permissions.DepartmentView"/> without <see cref="Permissions.RegisterView"/>
    /// is what limits what they see to the places they have been given.
    /// </summary>
    private static readonly IReadOnlySet<string> Ward = new HashSet<string>(StringComparer.Ordinal)
    {
        Permissions.DepartmentView,
        Permissions.WorkOrdersView,
        Permissions.WorkOrdersReport,
        Permissions.WorkOrdersNote,
        Permissions.IncidentsView,
        Permissions.IncidentsReport,
    };

    /// <summary>Keeping the installation running, and the people who may sign in to it. Nothing about the equipment.</summary>
    private static readonly IReadOnlySet<string> Installation = new HashSet<string>(StringComparer.Ordinal)
    {
        Permissions.StaffManage,
        Permissions.SystemBackups,
        Permissions.SystemRestore,
        Permissions.SystemUpdates,
        Permissions.SystemDiagnostics,
        Permissions.SystemLicence,
        Permissions.AuditView,
    };

    /// <summary>
    /// What the head of the department decides: the register, the schedules, the money, the training,
    /// the reports, and the department's own people. Not how the installation is run (backups,
    /// updates, the licence) and not the right to give other people access.
    /// </summary>
    private static readonly IReadOnlySet<string> Department =
        new HashSet<string>(
            Permissions.All.Where(p => p != Permissions.AccessManage
                         && (p == Permissions.StaffManage || !Installation.Contains(p))),
            StringComparer.Ordinal);

    /// <summary>
    /// What one role may do. An unknown role may do nothing.
    ///
    /// The head of Biomedical decides what the department does and does not run the installation:
    /// backups, restore, updates, diagnostics and the licence are the IT team's (and the Developer's).
    /// A hospital with no IT team can still be given one of those sections by name.
    ///
    /// A department user sees only their own departments' equipment and requests (see
    /// <see cref="Ward"/>).
    /// </summary>
    public static IReadOnlySet<string> For(string? role) => role switch
    {
        Roles.Developer => Everything,
        Roles.BmeHead => Department,
        Roles.ItAdmin => Installation,
        Roles.BmeEngineer => Floor,
        Roles.DepartmentUser => Ward,
        _ => None,
    };

    /// <summary>What a person holding these roles may do: everything any of them may.</summary>
    public static IReadOnlySet<string> For(IEnumerable<string> roles)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            all.UnionWith(For(role));
        }

        return all;
    }

    public static bool Has(IEnumerable<string> roles, string permission) =>
        roles.Any(role => For(role).Contains(permission));
}
