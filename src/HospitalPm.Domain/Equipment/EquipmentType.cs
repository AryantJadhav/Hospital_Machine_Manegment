namespace HospitalPm.Domain.Equipment;

/// <summary>
/// A model-independent kind of device ("Ventilator", "Ultrasound scanner").
/// Checklist templates attach here, never to <see cref="Category"/> — a
/// ventilator PM checklist is type-specific, and "Therapeutic" is far too
/// broad to check against.
/// </summary>
public sealed class EquipmentType
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    /// <summary>Stable machine key ("ventilator"). Excel import matches on this.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// True for types shipped with the product, false for ones a hospital
    /// added. Lets an upgrade refresh the seeded set without touching local
    /// additions.
    /// </summary>
    public bool IsSeeded { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public ICollection<EquipmentTypeCategory> Categories { get; set; } = [];
}
