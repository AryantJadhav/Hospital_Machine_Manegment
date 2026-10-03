using HospitalPm.Domain.Inventory;
using EquipmentAsset = HospitalPm.Domain.Assets.Equipment;

namespace HospitalPm.Domain.WorkOrders;

public enum WorkOrderStatus
{
    /// <summary>Raised by a ward, not yet picked up.</summary>
    Reported = 10,

    Assigned = 20,

    InProgress = 30,

    /// <summary>Waiting on a part, a vendor, or access to the machine.</summary>
    OnHold = 40,

    /// <summary>Engineer believes it is fixed. Not yet accepted.</summary>
    Resolved = 50,

    /// <summary>Accepted and finished. Terminal.</summary>
    Closed = 60,

    /// <summary>Raised in error or superseded. Terminal.</summary>
    Cancelled = 70,
}

public enum WorkOrderPriority
{
    Low = 10,
    Medium = 20,
    High = 30,

    /// <summary>
    /// Equipment is unusable and clinical work is blocked.
    ///
    /// About the machine's availability, never about a patient. No clinical
    /// detail belongs on a work order.
    /// </summary>
    Critical = 40,
}

/// <summary>
/// A breakdown ticket: something is wrong with a machine.
///
/// Separate from <see cref="Maintenance.PmTask"/> on purpose. A PM is
/// scheduled work against a checklist; a work order is unscheduled work
/// against a fault. Conflating them makes both compliance reporting and
/// downtime reporting wrong, because a hospital needs to know how much of
/// its engineering time is planned and how much is firefighting.
/// </summary>
public sealed class WorkOrder
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    /// <summary>
    /// Human-quotable reference, "WO-2026-000042".
    ///
    /// Assigned by a database default from a sequence, so two people raising
    /// a ticket at the same moment cannot collide. Deliberately not reset
    /// each year: a per-year counter needs a read-then-write that races, and
    /// uniqueness matters more than the cosmetics of the number.
    /// </summary>
    public string Number { get; set; } = string.Empty;

    public int EquipmentId { get; set; }

    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Reported;

    public WorkOrderPriority Priority { get; set; } = WorkOrderPriority.Medium;

    /// <summary>What the ward reported. Their words, not a diagnosis.</summary>
    public required string FaultDescription { get; set; }

    /// <summary>
    /// What kind of breakdown it was: hardware, software, both, an accessory or consumable, or improper usage.
    /// Blank until someone knows; the reporter may say, and the engineer who looks at the machine can.
    /// </summary>
    public BreakdownType? BreakdownType { get; set; }

    public int ReportedByUserId { get; set; }

    public DateTime ReportedAtUtc { get; set; }

    public int? AssignedToUserId { get; set; }

    public DateTime? AssignedAtUtc { get; set; }

    public DateTime? StartedAtUtc { get; set; }

    /// <summary>What was actually wrong and what was done about it.</summary>
    public string? ResolutionNotes { get; set; }

    public int? ResolvedByUserId { get; set; }

    public DateTime? ResolvedAtUtc { get; set; }

    public DateTime? ClosedAtUtc { get; set; }

    /// <summary>
    /// When the machine stopped being usable, and when it was usable again.
    ///
    /// Tracked on the ticket rather than derived from status timestamps,
    /// because they are not the same thing: a machine can keep working while
    /// a non-urgent fault is open, and can be down for hours before anyone
    /// raises a ticket. Uptime reporting needs the real interval.
    /// </summary>
    public DateTime? OutOfServiceAtUtc { get; set; }

    public DateTime? BackInServiceAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public EquipmentAsset? Equipment { get; set; }

    public ICollection<WorkOrderNote> Notes { get; set; } = [];

    public ICollection<WorkOrderPart> PartsUsed { get; set; } = [];

    public ICollection<WorkOrderAttachment> Attachments { get; set; } = [];

    /// <summary>Minutes the machine was unavailable, when both ends are known.</summary>
    public int? DowntimeMinutes =>
        OutOfServiceAtUtc is { } from && BackInServiceAtUtc is { } to && to > from
            ? (int)(to - from).TotalMinutes
            : null;
}

/// <summary>
/// One entry on a work order's timeline.
///
/// Append-only. The audit log records that a field changed; this records what
/// a person said and why, which is what the next engineer to pick the ticket
/// up actually needs.
/// </summary>
public sealed class WorkOrderNote
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int WorkOrderId { get; set; }

    public required string Body { get; set; }

    /// <summary>Set when the note accompanied a status change.</summary>
    public WorkOrderStatus? StatusAfter { get; set; }

    public int AuthorUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public WorkOrder? WorkOrder { get; set; }
}

/// <summary>
/// A spare drawn from the shelf against this ticket: what was used, how many,
/// and what it cost the department at the time. Recording it moves the same
/// count off <see cref="SparePart.QuantityOnHand"/>, so the register and the
/// repair history never disagree about what is left on the shelf.
/// </summary>
public sealed class WorkOrderPart
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int WorkOrderId { get; set; }

    public int SparePartId { get; set; }

    public int QuantityUsed { get; set; }

    /// <summary>
    /// The part's unit cost at the moment it was drawn, copied rather than
    /// looked up live, so a later price change on the shelf does not rewrite
    /// what a past repair is reported to have cost.
    /// </summary>
    public decimal? UnitCostAtUse { get; set; }

    public int UsedByUserId { get; set; }

    public DateTime UsedAtUtc { get; set; }

    public WorkOrder? WorkOrder { get; set; }

    public SparePart? SparePart { get; set; }
}

/// <summary>
/// A photo of the fault or the repair, attached to the ticket - what a ward
/// or engineer shows for a jammed latch or a scorched connector rather than
/// describing it in words.
///
/// This is only the description of the file. The bytes are in
/// <see cref="WorkOrderAttachmentData"/>, a separate table, so the audit
/// trigger that copies every changed row into the audit log copies a few
/// lines of text here and not a whole photo a second time.
///
/// Never edited, only added and removed - the same rule as a PM's report
/// file. NO PATIENT DATA: a photo of the machine is fine; one that shows a
/// patient is not. See CLAUDE.md.
/// </summary>
public sealed class WorkOrderAttachment
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int WorkOrderId { get; set; }

    /// <summary>The name it had when uploaded, cleaned of any folder and control characters.</summary>
    public required string FileName { get; set; }

    /// <summary>jpeg, png or webp, decided from the bytes themselves and not from what the uploader said.</summary>
    public required string ContentType { get; set; }

    public int SizeBytes { get; set; }

    public int UploadedByUserId { get; set; }

    public DateTime UploadedAtUtc { get; set; }

    public WorkOrder? WorkOrder { get; set; }

    public WorkOrderAttachmentData? Data { get; set; }
}

/// <summary>The bytes of an attachment. Stored in the database so backup and restore carry them.</summary>
public sealed class WorkOrderAttachmentData
{
    public int AttachmentId { get; set; }

    public int TenantId { get; set; } = 1;

    public required byte[] Data { get; set; }

    public WorkOrderAttachment? Attachment { get; set; }
}
