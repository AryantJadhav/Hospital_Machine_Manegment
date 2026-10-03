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
        Permissions.WorkOrdersWork,
        Permissions.EquipmentMove,
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
    /// A department user holds nothing yet: what they may see is limited to their own departments,
    /// and until that limit is in place they are given no way to read the register at all.
    /// </summary>
    public static IReadOnlySet<string> For(string? role) => role switch
    {
        Roles.Developer => Everything,
        Roles.BmeHead => Department,
        Roles.ItAdmin => Installation,
        Roles.BmeEngineer => Floor,
        Roles.DepartmentUser => None,
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
