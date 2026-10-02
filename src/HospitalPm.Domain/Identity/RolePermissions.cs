namespace HospitalPm.Domain.Identity;

/// <summary>
/// Which permissions each role holds. The one place that says so.
///
/// An Administrator holds all of them. An Employee holds none: what an Employee does - PM rounds,
/// faults, reading the register - is open to everyone signed in and needs no permission.
/// </summary>
public static class RolePermissions
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> Everything =
        new HashSet<string>(Permissions.All, StringComparer.Ordinal);

    /// <summary>What one role may do. An unknown role may do nothing.</summary>
    public static IReadOnlySet<string> For(string? role) => role switch
    {
        Roles.Admin => Everything,
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
