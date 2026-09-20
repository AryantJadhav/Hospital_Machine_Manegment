using HospitalPm.Domain.Maintenance;

namespace HospitalPm.Domain.Assets;

public enum DiagnosisOutcome
{
    /// <summary>Checked, and nothing wrong.</summary>
    Working = 10,

    /// <summary>Usable, but something needs following up.</summary>
    NeedsAttention = 20,

    /// <summary>Not fit to use.</summary>
    NotWorking = 30,
}

/// <summary>
/// One engineer's check of one machine: what was checked, what was found, and the
/// verdict. The daily round is a run of these.
///
/// Like a completed PM it is a record, not a working document: it names the exact
/// checklist version it was filled under and cannot be changed afterwards. A
/// mistake is put right by checking again, which leaves both on file.
///
/// NO PATIENT DATA. See CLAUDE.md.
/// </summary>
public sealed class Diagnosis
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int EquipmentId { get; set; }

    /// <summary>The checklist version the engineer was actually shown.</summary>
    public int ChecklistTemplateVersionId { get; set; }

    /// <summary>Answers keyed by checklist item key, as for a PM.</summary>
    public Dictionary<string, ChecklistAnswer> Answers { get; set; } = [];

    public DiagnosisOutcome Outcome { get; set; }

    public string? Notes { get; set; }

    /// <summary>Where the machine was when it was checked, by name at the time.</summary>
    public int LocationId { get; set; }

    public int PerformedByUserId { get; set; }

    public DateTime PerformedAtUtc { get; set; }

    public Guid? ClientSubmissionId { get; set; }

    public int OutOfRangeCount => Answers.Count(a => a.Value.OutOfRange);

    public int FailedCheckCount => Answers.Count(a => a.Value.IsFailedCheck());
}
