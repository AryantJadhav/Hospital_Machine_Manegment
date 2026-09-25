namespace HospitalPm.Domain.Maintenance;

/// <summary>
/// Who carries out a PM: the hospital's own team, or the vendor the machine's maintenance
/// contract (AMC or CMC) is with.
///
/// Numbered with gaps, like the other enums, so a value can be added later without renumbering.
/// </summary>
public enum PmPerformedBy
{
    /// <summary>The department's own engineers, who fill in the checklist and sign.</summary>
    InHouse = 10,

    /// <summary>The contract vendor. Their service report is the record.</summary>
    Vendor = 20,
}

/// <summary>
/// A file attached to a PM: the report a contract vendor hands over, as a PDF or a photo.
///
/// This is only the description of the file. The bytes are in <see cref="PmTaskAttachmentData"/>,
/// a separate table, so the audit trigger that copies every changed row into the audit log
/// copies a few lines of text here and not a whole PDF a second time.
///
/// Never edited. A wrongly uploaded file is removed by an Administrator and uploaded again,
/// and the removal is in the audit log.
///
/// NO PATIENT DATA. A photograph of a service report is fine; one that shows a patient is
/// not, and the upload page says so. See CLAUDE.md.
/// </summary>
public sealed class PmTaskAttachment
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int PmTaskId { get; set; }

    /// <summary>The name it had when uploaded, cleaned of any folder and control characters.</summary>
    public required string FileName { get; set; }

    /// <summary>
    /// pdf, jpeg, png or webp, decided from the bytes themselves and not from what the
    /// uploader said it was.
    /// </summary>
    public required string ContentType { get; set; }

    public int SizeBytes { get; set; }

    public int UploadedByUserId { get; set; }

    public DateTime UploadedAtUtc { get; set; }

    public PmTask? Task { get; set; }

    public PmTaskAttachmentData? Data { get; set; }
}

/// <summary>The bytes of an attachment. Stored in the database so backup and restore carry them.</summary>
public sealed class PmTaskAttachmentData
{
    public int AttachmentId { get; set; }

    public int TenantId { get; set; } = 1;

    public required byte[] Data { get; set; }

    public PmTaskAttachment? Attachment { get; set; }
}
