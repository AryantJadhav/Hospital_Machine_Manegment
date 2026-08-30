namespace HospitalPm.Domain.Maintenance;

/// <summary>
/// One technician's answer to one checklist item.
/// </summary>
public sealed class ChecklistAnswer
{
    /// <summary>
    /// The answer as the technician gave it, held as text regardless of the
    /// item type.
    ///
    /// Text rather than a typed union because this document has to be
    /// readable in ten years by whatever code exists then. A number that was
    /// entered as "42.5" stays "42.5"; the item type on the pinned checklist
    /// version says how to interpret it, and that version is immutable.
    /// </summary>
    public required string Value { get; set; }

    /// <summary>Optional note the technician added against this item.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// True when a numeric reading fell outside the item's acceptable range.
    ///
    /// Computed at submission and stored, not derived on read: the range
    /// lives on the checklist version, and while that version is immutable,
    /// storing the verdict means a report never has to re-derive a
    /// pass/fail judgement that was already made at the bedside.
    /// </summary>
    public bool OutOfRange { get; set; }
}

/// <summary>
/// The filled-in checklist. Evidence, not a working document.
///
/// Kept separate from <see cref="PmTask"/> deliberately. The task is a
/// schedule occurrence and is queried constantly for the work list; the
/// completion carries the answers and a signature image and is read only
/// when someone opens the record. Keeping them apart keeps the hot query
/// small.
/// </summary>
public sealed class PmCompletion
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int PmTaskId { get; set; }

    /// <summary>
    /// The exact checklist version the technician filled in.
    ///
    /// Recorded from what the device actually rendered, not from whatever is
    /// published at the moment the submission lands. A version can be
    /// superseded while a technician is mid-round on a ward with no signal,
    /// and the honest record is the questions they were actually asked.
    /// </summary>
    public int ChecklistTemplateVersionId { get; set; }

    /// <summary>Answers keyed by checklist item key.</summary>
    public Dictionary<string, ChecklistAnswer> Answers { get; set; } = [];

    /// <summary>
    /// PNG of the technician's signature, drawn on the device.
    ///
    /// Stored in the database as bytes rather than in object storage: a
    /// client install is allowed two services, and adding MinIO or an S3
    /// dependency for a 20 kB image would break that for no benefit. It is
    /// also part of the evidence document and should not be able to go
    /// missing separately from it.
    /// </summary>
    public byte[]? SignaturePng { get; set; }

    /// <summary>Printed name captured alongside the signature.</summary>
    public string? SignedByName { get; set; }

    public int CompletedByUserId { get; set; }

    public DateTime CompletedAtUtc { get; set; }

    /// <summary>
    /// When the technician actually finished, as reported by the device.
    ///
    /// Differs from <see cref="CompletedAtUtc"/> when a submission was queued
    /// offline and replayed later. Both are kept: the server time is when the
    /// record was received, the device time is when the work was done, and a
    /// compliance report needs the second one.
    /// </summary>
    public DateTime? PerformedAtUtc { get; set; }

    /// <summary>
    /// Client-generated id for the submission.
    ///
    /// The mobile app queues writes when signal drops and replays them in
    /// order. Without this, a replay after a response was lost in transit
    /// would either fail as a duplicate or record the PM twice. With it,
    /// replaying returns the original result.
    /// </summary>
    public Guid? ClientSubmissionId { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public PmTask? Task { get; set; }

    /// <summary>Items answered outside their acceptable range.</summary>
    public int OutOfRangeCount => Answers.Count(a => a.Value.OutOfRange);
}
