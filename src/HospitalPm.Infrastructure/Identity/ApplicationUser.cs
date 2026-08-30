using Microsoft.AspNetCore.Identity;

namespace HospitalPm.Infrastructure.Identity;

/// <summary>
/// A person who logs in: biomedical staff, never a patient.
/// </summary>
public sealed class ApplicationUser : IdentityUser<int>
{
    /// <summary>Always 1 for now. Carried on every table so tenancy never has to be retrofitted.</summary>
    public int TenantId { get; set; } = 1;

    public required string FullName { get; set; }

    /// <summary>Employee or staff code as the hospital knows it. Not unique across tenants by design.</summary>
    public string? StaffCode { get; set; }

    /// <summary>
    /// Deactivation rather than deletion. A technician who leaves must stay
    /// resolvable: their name appears on completed work orders and PM
    /// certificates that have to remain readable for years.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public DateTime? LastLoginAtUtc { get; set; }
}
