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

    /// <summary>
    /// True when a Pass/Fail check was answered "fail".
    ///
    /// Derived from the stored text rather than added as another stored flag,
    /// which is what lets it apply to every completion already on file. Only a
    /// Pass/Fail item can hold "fail" (Yes/No holds yes, no or na), so the value
    /// names a failed check without needing the item type. A method rather than
    /// a property so it is never written into the stored document.
    ///
    /// Not the same as OutOfRange, which only a numeric reading can set. A
    /// completion whose every reading was in range but whose alarm test failed
    /// was reported as fully clean, because nothing looked at the answer.
    /// </summary>
    public bool IsFailedCheck() => string.Equals(Value, "fail", StringComparison.OrdinalIgnoreCase);
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
    /// The technician's signature as drawn on the device.
    ///
    /// Stored in the database as bytes rather than in object storage: a
    /// client install is allowed two services, and adding MinIO or an S3
    /// dependency for a 20 kB image would break that for no benefit. It is
    /// also part of the evidence document and should not be able to go
    /// missing separately from it.
    /// </summary>
    public byte[]? Signature { get; set; }

    /// <summary>
    /// "png" or "svg".
    ///
    /// Two formats because the two clients capture differently. A browser
    /// canvas produces a PNG in one call; a React Native pad produces stroke
    /// paths, and rasterising them on the device would mean a WebView or a
    /// native module for no gain — the strokes are the signature, and SVG
    /// keeps them exact and small.
    /// </summary>
    public string? SignatureFormat { get; set; }

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

    /// <summary>
    /// Who did the work. A vendor's PM has no checklist answers and no drawn signature: their
    /// service report, attached to the PM, is the record, and <see cref="SignedByName"/> is
    /// their engineer's name.
    /// </summary>
    public PmPerformedBy PerformedBy { get; set; } = PmPerformedBy.InHouse;

    /// <summary>
    /// The vendor's name as it was when this was recorded. Copied rather than read from the
    /// machine, because the machine's contract can change and the record must not.
    /// </summary>
    public string? VendorName { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public PmTask? Task { get; set; }

    /// <summary>Items answered outside their acceptable range.</summary>
    public int OutOfRangeCount => Answers.Count(a => a.Value.OutOfRange);

    /// <summary>Pass/Fail checks answered "fail".</summary>
    public int FailedCheckCount => Answers.Count(a => a.Value.IsFailedCheck());
}
