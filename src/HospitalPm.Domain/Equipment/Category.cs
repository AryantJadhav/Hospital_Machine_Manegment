namespace HospitalPm.Domain.Equipment;

/// <summary>
/// A functional classification of medical devices (Diagnostic, Therapeutic,
/// Imaging, ...). Fixed, seeded set — hospitals do not add categories.
/// </summary>
public sealed class Category
{
    public int Id { get; set; }

    /// <summary>Always 1 for now. Carried on every table so tenancy never has to be retrofitted.</summary>
    public int TenantId { get; set; } = 1;

    /// <summary>Stable machine key ("diagnostic"). Never renamed — imports and reports key off it.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public int DisplayOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public ICollection<EquipmentTypeCategory> EquipmentTypes { get; set; } = [];
}
