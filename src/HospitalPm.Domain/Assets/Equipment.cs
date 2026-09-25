using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Locations;

namespace HospitalPm.Domain.Assets;

/// <summary>
/// A single physical machine on the hospital's register.
///
/// NO PATIENT DATA. Equipment is linked to a <see cref="Location"/> — a
/// department or room — and never to a patient. There is deliberately no
/// field here for patient name, ID, MRN, or any clinical detail, and adding
/// one would reintroduce a compliance burden this product exists without.
/// See CLAUDE.md.
/// </summary>
public sealed class Equipment
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    /// <summary>
    /// The hospital's own asset tag, as printed on the QR label. Unique per
    /// tenant: two machines cannot carry the same tag, or a scan is
    /// ambiguous and the technician cannot tell which one they are standing
    /// in front of.
    /// </summary>
    public required string AssetTag { get; set; }

    /// <summary>
    /// Manufacturer serial. Not unique — hospitals routinely have blanks,
    /// duplicates from different manufacturers, and transcription errors in
    /// the spreadsheet they hand over on day one. Enforcing uniqueness here
    /// would reject their real data at import.
    /// </summary>
    public string? SerialNumber { get; set; }

    public int EquipmentTypeId { get; set; }

    /// <summary>
    /// Where the machine is. Required: an asset with no location cannot be
    /// found by the technician sent to service it.
    /// </summary>
    public int LocationId { get; set; }

    public string? Manufacturer { get; set; }

    public string? Model { get; set; }

    public EquipmentStatus Status { get; set; } = EquipmentStatus.InService;

    /// <summary>
    /// Critical, semi-critical or non-critical. Null for a machine nobody has
    /// classified yet, which is every machine registered before this field
    /// existed and any imported from a spreadsheet that has no such column.
    /// </summary>
    public EquipmentCriticality? Criticality { get; set; }

    public DateOnly? PurchaseDate { get; set; }

    /// <summary>
    /// What the machine cost, in rupees. Null when nobody knows: an older register
    /// often has no figure, and a blank is truer than a zero.
    /// </summary>
    public decimal? PurchaseCost { get; set; }

    public DateOnly? InstallationDate { get; set; }

    public DateOnly? WarrantyExpiryDate { get; set; }

    /// <summary>
    /// Whether the machine is insured. When false the three insurance details below
    /// are all empty; the database enforces it, so a machine that says it is not
    /// insured cannot still carry a policy that appears to be current.
    /// </summary>
    public bool IsInsured { get; set; }

    /// <summary>The insurer. Set only when <see cref="IsInsured"/>.</summary>
    public string? InsuranceProvider { get; set; }

    /// <summary>The policy's number, for the person who has to make a claim. Optional even when insured.</summary>
    public string? InsurancePolicyNumber { get; set; }

    /// <summary>The last day the policy covers the machine. Set whenever <see cref="IsInsured"/>.</summary>
    public DateOnly? InsuranceExpiryDate { get; set; }

    /// <summary>What the policy costs, in rupees. Set only when <see cref="IsInsured"/>, and optional even then.</summary>
    public decimal? InsuranceCost { get; set; }

    /// <summary>
    /// AMC or CMC, or null when the machine is not under a maintenance contract. When
    /// null, every other maintenance-contract field is empty; the database enforces it.
    /// </summary>
    public MaintenanceContractType? MaintenanceContractType { get; set; }

    /// <summary>The company that services the machine under the contract.</summary>
    public string? MaintenanceVendor { get; set; }

    /// <summary>The contract's own number, for the phone call when the machine fails. Optional.</summary>
    public string? MaintenanceContractNumber { get; set; }

    public DateOnly? MaintenanceStartDate { get; set; }

    /// <summary>The last day the contract covers. Never before <see cref="MaintenanceStartDate"/>.</summary>
    public DateOnly? MaintenanceEndDate { get; set; }

    /// <summary>What the contract costs, in rupees. Optional.</summary>
    public decimal? MaintenanceCost { get; set; }

    /// <summary>Free-text notes from the biomedical team. Not a clinical record.</summary>
    public string? Notes { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public EquipmentType? EquipmentType { get; set; }

    public Location? Location { get; set; }
}
