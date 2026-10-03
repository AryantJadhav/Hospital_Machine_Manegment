namespace HospitalPm.Domain.Identity;

/// <summary>
/// One permission given to one person, or taken from them, on top of what their role holds.
///
/// A role says what most people in a job may do. This is for the person who needs one section more,
/// or one less: an engineer who is asked to run the monthly report, a head of department who should
/// not touch the licence. At most one row for each person and permission.
///
/// Only the Developer writes these, and the hospital can see them (the Staff page lists them, and
/// every change is in the audit log written by the database). Never a way in that the hospital
/// cannot see.
/// </summary>
public sealed class PermissionGrant
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int UserId { get; set; }

    /// <summary>One of <see cref="Permissions.All"/>, and one that <see cref="PermissionCatalog"/> says may be granted.</summary>
    public required string Permission { get; set; }

    /// <summary><see cref="GrantEffect.Grant"/> or <see cref="GrantEffect.Revoke"/>.</summary>
    public required string Effect { get; set; }

    /// <summary>The last day it applies, in the hospital's own date. Null: until it is taken away.</summary>
    public DateOnly? ExpiresOn { get; set; }

    /// <summary>Who asked, or why: "Asked by Dr Rao for the Q3 review".</summary>
    public string? Note { get; set; }

    public int GrantedByUserId { get; set; }

    public DateTime GrantedAtUtc { get; set; }
}

public static class GrantEffect
{
    /// <summary>The person may, though their role does not.</summary>
    public const string Grant = "Grant";

    /// <summary>The person may not, though their role does.</summary>
    public const string Revoke = "Revoke";

    public static bool IsKnown(string? value) => value is Grant or Revoke;
}
