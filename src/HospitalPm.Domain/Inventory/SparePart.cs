using HospitalPm.Domain.Equipment;

namespace HospitalPm.Domain.Inventory;

/// <summary>
/// One kind of spare part the biomedical department keeps on its own shelf -
/// fuses, tubing sets, sensor probes, filters - and how many of it there are
/// right now.
///
/// Not one row per physical item: a shelf holding forty of the same fuse is
/// one row with <see cref="QuantityOnHand"/> 40, the way a hospital's own
/// stock register works. That is also why this is never deleted once real
/// stock has moved through it - a part run down to zero is still a part the
/// department stocks, just not right now - so it is retired
/// (<see cref="IsActive"/> false) instead, and drops out of the pickers.
///
/// NO PATIENT DATA. A part is linked to an equipment type, never to a
/// patient or a specific repair's clinical detail. See CLAUDE.md.
/// </summary>
public sealed class SparePart
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    /// <summary>The department's own stock code, as written on the bin label. Unique per tenant.</summary>
    public required string PartNumber { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>
    /// What it fits. Optional: a lot of stock - screws, cable ties, generic
    /// tubing - is not specific to one kind of machine.
    /// </summary>
    public int? EquipmentTypeId { get; set; }

    /// <summary>"pcs", "box", "metre" - whatever the department counts it in.</summary>
    public string Unit { get; set; } = "pcs";

    public int QuantityOnHand { get; set; }

    /// <summary>
    /// Below this, the part is short. Zero means "tell me only when it runs out
    /// completely" - a fair choice for something bought once a decade.
    /// </summary>
    public int ReorderLevel { get; set; }

    /// <summary>Cost per unit, in rupees. Null when nobody has recorded one.</summary>
    public decimal? UnitCost { get; set; }

    public string? Supplier { get; set; }

    /// <summary>
    /// The day the stock on the shelf was bought. The parts are counted, not tracked one by one, so
    /// this is the date of the purchase the department last recorded, not of every item. Null when
    /// nobody has written it down.
    /// </summary>
    public DateOnly? PurchaseDate { get; set; }

    /// <summary>
    /// How long the supplier's warranty runs from the purchase date, in months. Null when there is none
    /// or it is not known. It only means something with a <see cref="PurchaseDate"/> to run from.
    /// </summary>
    public int? WarrantyMonths { get; set; }

    /// <summary>The last day the warranty covers, when there is a purchase date and a warranty to work it from.</summary>
    public DateOnly? WarrantyExpiryDate =>
        PurchaseDate is { } bought && WarrantyMonths is { } months ? bought.AddMonths(months) : null;

    /// <summary>Where it physically sits - "Store Room A, Rack 3". Free text; there is no shelf register to key against.</summary>
    public string? StorageLocation { get; set; }

    public bool IsActive { get; set; } = true;

    public string? Notes { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public EquipmentType? EquipmentType { get; set; }
}
