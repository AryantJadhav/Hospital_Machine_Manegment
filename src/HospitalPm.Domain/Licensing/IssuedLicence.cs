namespace HospitalPm.Domain.Licensing;

/// <summary>
/// A licence we have issued, kept on the copy of the software that holds the signing key, so there is a list of
/// who has one, and a place for each licence's lock and its counter.
///
/// Only the Developer's own copy ever has rows here. A hospital's installation has the table and never writes to
/// it, because it has no signing key to issue from.
/// </summary>
public sealed class IssuedLicence
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    /// <summary>The id inside the signed licence: what a hospital quotes, and what a lock code names.</summary>
    public Guid LicenceId { get; set; }

    public required string HospitalName { get; set; }

    public DateOnly IssuedOn { get; set; }

    /// <summary>The last day, or null when it ends a number of days after install (see <see cref="DurationDays"/>) or never.</summary>
    public DateOnly? ExpiresOn { get; set; }

    public int? DurationDays { get; set; }

    public int? MaxEquipment { get; set; }

    /// <summary>Module keys, comma separated. Empty for core only.</summary>
    public string Modules { get; set; } = string.Empty;

    public string? Notes { get; set; }

    /// <summary>The licence exactly as sent, so it can be sent again without signing a second one.</summary>
    public required string LicenceText { get; set; }

    public int IssuedByUserId { get; set; }

    public DateTime IssuedAtUtc { get; set; }

    /// <summary>The number on the last lock or unlock code made for this licence. Only goes up. Zero: none made.</summary>
    public long LockSequence { get; set; }

    /// <summary>What the last code made for it said. Whether the hospital has entered it, we cannot know.</summary>
    public bool IsLocked { get; set; }

    public DateTime? LockChangedAtUtc { get; set; }
}
