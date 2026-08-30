namespace HospitalPm.Domain.Equipment;

/// <summary>
/// Links an <see cref="EquipmentType"/> to a <see cref="Category"/>.
///
/// Many-to-many because the categories genuinely overlap: an ultrasound is
/// both Imaging and Diagnostic; a neonatal ventilator is Neonatal,
/// Therapeutic and Life-support. Exactly one link per type is marked
/// primary, which drives default reporting grouping; the rest power
/// filtering and audit evidence.
/// </summary>
public sealed class EquipmentTypeCategory
{
    public int EquipmentTypeId { get; set; }

    public int CategoryId { get; set; }

    public int TenantId { get; set; } = 1;

    /// <summary>
    /// Enforced by a partial unique index: exactly one primary per type.
    /// </summary>
    public bool IsPrimary { get; set; }

    public EquipmentType? EquipmentType { get; set; }

    public Category? Category { get; set; }
}
