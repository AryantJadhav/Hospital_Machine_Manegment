namespace HospitalPm.Domain.Identity;

/// <summary>
/// What one person may do: their role's permissions, plus what they have been given, minus what has
/// been taken away. The one place that sum is done.
/// </summary>
public static class EffectivePermissions
{
    public static IReadOnlySet<string> For(IEnumerable<string> roles, IEnumerable<PermissionGrant> grants, DateOnly today)
    {
        var roleList = roles.ToList();
        var set = new HashSet<string>(RolePermissions.For(roleList), StringComparer.Ordinal);

        // A Developer holds everything, and nothing given or taken changes that.
        if (roleList.Contains(Roles.Developer))
        {
            return set;
        }

        foreach (var g in grants)
        {
            // A grant that has run out, or that names something that cannot be given, does nothing.
            if (!IsActive(g, today) || !PermissionCatalog.IsGrantable(g.Permission))
            {
                continue;
            }

            if (g.Effect == GrantEffect.Grant)
            {
                set.Add(g.Permission);
            }
            else if (g.Effect == GrantEffect.Revoke)
            {
                set.Remove(g.Permission);
            }
        }

        return set;
    }

    /// <summary>Still in force today? The expiry day itself counts: "until the 31st" includes the 31st.</summary>
    public static bool IsActive(PermissionGrant g, DateOnly today) => g.ExpiresOn is null || g.ExpiresOn >= today;
}
