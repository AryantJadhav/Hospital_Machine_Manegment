using HospitalPm.Domain.Equipment;

namespace HospitalPm.Domain.Checklists;

/// <summary>
/// A named checklist for an equipment type — "Quarterly PM", "Electrical
/// safety test". An equipment type can have several.
///
/// This row is the stable identity. It carries no questions; those live on
/// versions, because the questions change and completed checklists must
/// still render against the exact wording they were filled under.
///
/// Attached to <see cref="EquipmentType"/>, never to a category: a
/// ventilator's PM checklist is specific to ventilators, and "Therapeutic"
/// is far too broad to check against.
/// </summary>
public sealed class ChecklistTemplate
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int EquipmentTypeId { get; set; }

    /// <summary>Stable machine key, unique per tenant.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>What the checklist is for: a scheduled PM, or the everyday check of a machine.</summary>
    public ChecklistKind Kind { get; set; } = ChecklistKind.Pm;

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public EquipmentType? EquipmentType { get; set; }

    public ICollection<ChecklistTemplateVersion> Versions { get; set; } = [];
}

public enum ChecklistKind
{
    /// <summary>Preventive maintenance, run from a schedule and signed.</summary>
    Pm = 10,

    /// <summary>The everyday check an engineer makes of a machine on a round.</summary>
    Diagnosis = 20,
}

public enum ChecklistVersionStatus
{
    /// <summary>Editable. Never used to record a completion.</summary>
    Draft = 10,

    /// <summary>Immutable and in use. At most one per template.</summary>
    Published = 20,

    /// <summary>
    /// Superseded. Still immutable, still readable, still referenced by every
    /// completion filled under it.
    /// </summary>
    Archived = 30,
}

/// <summary>
/// One immutable revision of a checklist's questions.
///
/// Once published, the definition can never change. This is the rule the
/// whole design exists to protect: a PM completed in 2026 has to render
/// exactly as the technician saw it when an auditor opens it in 2031, even
/// though the template has moved on seven versions since. Editing in place
/// would silently rewrite history.
///
/// Enforced by a database trigger, not by convention — application code is
/// not the right place to guarantee something an auditor relies on.
/// </summary>
public sealed class ChecklistTemplateVersion
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int ChecklistTemplateId { get; set; }

    /// <summary>1-based, assigned on publish. Drafts carry 0 until then.</summary>
    public int VersionNo { get; set; }

    public ChecklistVersionStatus Status { get; set; } = ChecklistVersionStatus.Draft;

    /// <summary>The questions, as JSONB.</summary>
    public ChecklistDefinition Definition { get; set; } = new();

    /// <summary>Free text from the author: what changed and why.</summary>
    public string? ChangeNote { get; set; }

    public DateTime? PublishedAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public ChecklistTemplate? Template { get; set; }

    public bool IsEditable => Status == ChecklistVersionStatus.Draft;
}
