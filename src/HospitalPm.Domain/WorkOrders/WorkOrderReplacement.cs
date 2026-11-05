using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Locations;
using EquipmentAsset = HospitalPm.Domain.Assets.Equipment;

namespace HospitalPm.Domain.WorkOrders;

/// <summary>
/// A machine put in place of a faulty one while the faulty one is being repaired.
///
/// A repair can take days. The department still has patients' care to run, so the biomedical engineer takes a free machine
/// from the store and places it where the faulty one stands, and takes it back when the repair is done. This row is the record
/// of that loan: which machine stood in, where it came from, where it was put, who did it, and when it went back.
///
/// It lives in its own table and not on the work order because the work order is locked once it is resolved, and the stand-in
/// is often taken back after that. It is kept for good (never deleted), so "which machine covered for that one, and for how
/// long?" can always be answered.
///
/// NO PATIENT DATA. It is about two machines and the places they were in.
/// </summary>
public sealed class WorkOrderReplacement
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int WorkOrderId { get; set; }

    /// <summary>The machine that stands in for the faulty one.</summary>
    public int ReplacementEquipmentId { get; set; }

    /// <summary>Where the stand-in was before: the store, normally. It goes back here.</summary>
    public int FromLocationId { get; set; }

    /// <summary>Where it was put: the place of the faulty machine.</summary>
    public int ToLocationId { get; set; }

    /// <summary>The stand-in's status on the register before it went out (in store, normally). Restored when it is taken back.</summary>
    public EquipmentStatus StatusBefore { get; set; }

    public DateTime ProvidedAtUtc { get; set; }

    public int ProvidedByUserId { get; set; }

    /// <summary>Anything worth saying about the loan: "Same model, calibrated last month".</summary>
    public string? Note { get; set; }

    /// <summary>When the stand-in was taken back. Null while it is still in place. Once set, the row is a finished record.</summary>
    public DateTime? ReturnedAtUtc { get; set; }

    public int? ReturnedByUserId { get; set; }

    public WorkOrder? WorkOrder { get; set; }

    public EquipmentAsset? ReplacementEquipment { get; set; }

    public Location? FromLocation { get; set; }

    public Location? ToLocation { get; set; }

    /// <summary>Still standing in for the faulty machine.</summary>
    public bool IsActive => ReturnedAtUtc is null;
}
