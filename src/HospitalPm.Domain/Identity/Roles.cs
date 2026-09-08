namespace HospitalPm.Domain.Identity;

/// <summary>
/// The two fixed roles.
///
/// There were four — Admin, BiomedicalHead, SeniorEngineer, Technician — which
/// modelled a large teaching hospital's hierarchy. The department this is sold
/// to is usually one head and three technicians, and four tiers meant an
/// administrator choosing between three near-identical options every time they
/// added someone. Three of them collapsed into Admin; nobody lost access.
///
/// The line between them: an Employee records what they did, an Admin decides
/// what gets done. Two consequences of that are not obvious and are worth
/// stating, because both are about the record rather than about trust:
///
///   - Skipping a PM is an Admin act. A skip is a permanent gap in the
///     maintenance record, and deciding a machine will not be maintained this
///     quarter is a management call, not a decision for whoever is holding the
///     work list.
///   - Assigning work is an Admin act, because it is a decision about someone
///     else's day.
///
/// Constants rather than an enum because Identity stores role names as strings
/// and every authorization check needs the literal — a typo'd string in
/// RequireRole fails open-ended and silently, which is the failure mode this
/// prevents.
/// </summary>
public static class Roles
{
    /// <summary>Everything, including staff accounts, backups and the licence.</summary>
    public const string Admin = "Admin";

    /// <summary>
    /// Works the floor: PM rounds, faults, and reading the register. Cannot
    /// change what the department has committed to.
    /// </summary>
    public const string Employee = "Employee";

    public static readonly IReadOnlyList<string> All = [Admin, Employee];
}
