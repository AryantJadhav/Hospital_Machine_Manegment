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
