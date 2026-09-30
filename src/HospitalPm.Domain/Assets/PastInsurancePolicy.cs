namespace HospitalPm.Domain.Assets;

/// <summary>
/// An insurance policy a machine no longer has, kept when the policy is renewed.
///
/// A machine's record holds one current policy. Renewing used to overwrite it, so the cost of the
/// old policy was gone and what a machine has cost in insurance over the years could not be added
/// up. Renewing now moves the old policy here first.
///
/// Never edited: it is what was in force, and what it cost.
/// </summary>
public sealed class PastInsurancePolicy
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int EquipmentId { get; set; }

    public required string Provider { get; set; }

    public string? PolicyNumber { get; set; }

    /// <summary>The last day the policy covered the machine.</summary>
    public DateOnly ExpiryDate { get; set; }

    /// <summary>What the policy cost, in rupees. Null when nobody recorded it.</summary>
    public decimal? Cost { get; set; }

    /// <summary>When it was renewed, and so replaced.</summary>
    public DateTime RenewedAtUtc { get; set; }

    public int RenewedByUserId { get; set; }

    public Equipment? Equipment { get; set; }
}
