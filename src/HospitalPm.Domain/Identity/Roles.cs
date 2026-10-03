namespace HospitalPm.Domain.Identity;

/// <summary>
/// The five kinds of user. One per account.
///
/// There were once four roles (Admin, BiomedicalHead, SeniorEngineer, Technician), collapsed to two
/// because they were four tiers inside one small team and an administrator kept choosing between
/// near-identical options. These five are not tiers of one team. They are different people, often
/// from different departments, who need different doors into the same system:
///
///   Developer       the person who built and supports the software.
///   ItAdmin         the hospital's IT team: keeps the installation running.
///   BmeHead         the head of the Biomedical department: decides what gets done.
///   BmeEngineer     a biomedical engineer: records what they did.
///   DepartmentUser  someone in a clinical or other department: reports faults on their own equipment.
///
/// What each may do is not decided at the endpoints: they ask for a <see cref="Permissions"/> name,
/// and <see cref="RolePermissions"/> says which role holds it.
///
/// Constants rather than an enum because Identity stores role names as strings and every check
/// needs the literal - a typo'd string in a check fails open-ended and silently, which is the
/// failure mode this prevents.
/// </summary>
public static class Roles
{
    /// <summary>Built and supports the software. Everything, including who may sign in as one.</summary>
    public const string Developer = "Developer";

    /// <summary>The hospital's IT team: backups, updates, the licence, diagnostics, and staff accounts.</summary>
    public const string ItAdmin = "ItAdmin";

    /// <summary>Head of the Biomedical department. Owns the register, the schedules and the reports.</summary>
    public const string BmeHead = "BmeHead";

    /// <summary>Works the floor: PM rounds, faults, and reading the register. Cannot change what the department has committed to.</summary>
    public const string BmeEngineer = "BmeEngineer";

    /// <summary>A person in another department, who reports faults on their own department's equipment.</summary>
    public const string DepartmentUser = "DepartmentUser";

    public static readonly IReadOnlyList<string> All = [Developer, ItAdmin, BmeHead, BmeEngineer, DepartmentUser];

    /// <summary>What a person reads on screen. The stored name has no spaces.</summary>
    public static string Label(string? role) => role switch
    {
        Developer => "Developer",
        ItAdmin => "IT team",
        BmeHead => "Head of Biomedical",
        BmeEngineer => "Biomedical engineer",
        DepartmentUser => "Department user",
        _ => role ?? string.Empty,
    };

    /// <summary>One line on what the role is for, for the Staff page.</summary>
    public static string Describe(string role) => role switch
    {
        Developer => "Built and supports the software. Full access.",
        ItAdmin => "The hospital's IT team: staff accounts, backups, updates, the licence and diagnostics.",
        BmeHead => "Head of Biomedical: the register, schedules, checklists, spare parts, training, reports and staff.",
        BmeEngineer => "Works the floor: PM rounds, faults, and reading the register.",
        DepartmentUser => "Reports faults on the equipment of their own department.",
        _ => string.Empty,
    };

    /// <summary>
    /// The roles someone holding <paramref name="role"/> may give to an account, change an account
    /// to, or manage the accounts of. Nobody may reach above themselves: only a Developer makes
    /// another Developer, and the head of the department manages the department's own people, not
    /// the hospital's IT team.
    /// </summary>
    public static IReadOnlyList<string> ManageableBy(string? role) => role switch
    {
        Developer => All,
        ItAdmin => [ItAdmin, BmeHead, BmeEngineer, DepartmentUser],
        BmeHead => [BmeEngineer, DepartmentUser],
        _ => [],
    };

    /// <summary>The roles that may manage staff, for those that hold several.</summary>
    public static IReadOnlyList<string> ManageableBy(IEnumerable<string> roles) =>
        roles.SelectMany(r => ManageableBy(r)).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// The roles whose accounts someone may stop from signing in, and let back in. That is everyone
    /// they manage, and, for the hospital's IT team, the Developer too.
    ///
    /// The Developer's account is visible in the hospital's staff list and the hospital holds the
    /// switch: the IT team can stop it, and it stays stopped until they let it back in. They cannot
    /// change it in any other way (no new password, no change of role), because that would hand the
    /// Developer's own account to someone else. A way in the hospital cannot see or close is not
    /// acceptable on a system that holds its equipment records.
    /// </summary>
    public static IReadOnlyList<string> PausableBy(IEnumerable<string> roles)
    {
        var list = roles.ToList();
        var pausable = ManageableBy(list).ToList();
        if (list.Contains(ItAdmin) && !pausable.Contains(Developer))
        {
            pausable.Add(Developer);
        }

        return pausable;
    }
}
