using HospitalPm.Domain.Checklists;

// Aliased because HospitalPm.Domain.Equipment is a namespace (equipment
// types and categories) while Equipment is a class in Domain.Assets, so a
// bare `Equipment` here resolves to the namespace. Worth renaming one of
// them eventually; not worth the churn mid-slice.
using EquipmentAsset = HospitalPm.Domain.Assets.Equipment;

namespace HospitalPm.Domain.Maintenance;

/// <summary>
/// A standing instruction: this machine gets this checklist, this often.
/// </summary>
public sealed class PmSchedule
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int EquipmentId { get; set; }

    /// <summary>
    /// The template, not a specific version. Which version applies is decided
    /// when the PM is actually completed, so a checklist updated between
    /// scheduling and execution is the one the technician fills in.
    /// </summary>
    public int ChecklistTemplateId { get; set; }

    public PmFrequency Frequency { get; set; } = PmFrequency.Quarterly;

    /// <summary>Used only when <see cref="Frequency"/> is Custom.</summary>
    public int IntervalDays { get; set; }

    /// <summary>
    /// The date every occurrence is measured from. Occurrences are anchored
    /// here rather than to the last completion, so the schedule cannot drift
    /// later each cycle.
    /// </summary>
    public DateOnly AnchorDate { get; set; }

    /// <summary>
    /// Days after the due date before a PM counts as late.
    ///
    /// Real hospitals do not stop a ward round to hit a date exactly, and a
    /// system that marks a PM overdue at one minute past midnight teaches
    /// people to ignore the overdue list. Zero means strict.
    /// </summary>
    public int GraceDays { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Who does this PM: the hospital's own team, or the contract vendor. A schedule can
    /// only be the vendor's on a machine that has a maintenance contract (AMC or CMC).
    /// </summary>
    public PmPerformedBy PerformedBy { get; set; } = PmPerformedBy.InHouse;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public EquipmentAsset? Equipment { get; set; }

    public ChecklistTemplate? ChecklistTemplate { get; set; }

    public ICollection<PmTask> Tasks { get; set; } = [];
}

public enum PmTaskStatus
{
    /// <summary>Generated, not yet due.</summary>
    Scheduled = 10,

    /// <summary>Due now, within grace.</summary>
    Due = 20,

    /// <summary>Past due plus grace.</summary>
    Overdue = 30,

    Completed = 40,

    /// <summary>
    /// Deliberately not done, with a reason. Distinct from Completed so a
    /// compliance report cannot quietly count a skip as a PM.
    /// </summary>
    Skipped = 50,
}

/// <summary>
/// One occurrence of a schedule — the thing a technician actually picks up.
/// </summary>
public sealed class PmTask
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int PmScheduleId { get; set; }

    /// <summary>
    /// Denormalised from the schedule so the work list can filter by location
    /// and equipment without joining through it, and so a task survives the
    /// schedule being deactivated.
    /// </summary>
    public int EquipmentId { get; set; }

    public DateOnly DueDate { get; set; }

    public PmTaskStatus Status { get; set; } = PmTaskStatus.Scheduled;

    public DateTime? CompletedAtUtc { get; set; }

    public int? CompletedByUserId { get; set; }

    /// <summary>
    /// The checklist version the PM was filled under, recorded at completion.
    /// Null until then. This is what makes a completed PM readable years
    /// later against exactly the questions that were asked.
    /// </summary>
    public int? ChecklistTemplateVersionId { get; set; }

    public string? SkipReason { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public PmSchedule? Schedule { get; set; }

    public EquipmentAsset? Equipment { get; set; }

    /// <summary>Status a task should hold on a given day, ignoring completion.</summary>
    public static PmTaskStatus StatusOn(DateOnly today, DateOnly dueDate, int graceDays)
    {
        if (today < dueDate)
        {
            return PmTaskStatus.Scheduled;
        }

        return today > dueDate.AddDays(graceDays) ? PmTaskStatus.Overdue : PmTaskStatus.Due;
    }
}
