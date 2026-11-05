namespace HospitalPm.Domain.GatePasses;

/// <summary>
/// A photo on a gate pass: what left, in what condition, or what came back. Optional: a pass is complete without one.
///
/// What was seen, not a document. The bytes are held in <see cref="GatePassAttachmentData"/>, so the list of a pass's photos
/// can be read without pulling the pictures. NO PATIENT DATA: a photo of goods, never of a person.
/// </summary>
public sealed class GatePassAttachment
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int GatePassId { get; set; }

    /// <summary>The name it had when uploaded, cleaned of any folder and control characters.</summary>
    public required string FileName { get; set; }

    /// <summary>jpeg, png or webp, decided from the bytes themselves and not from what the uploader said.</summary>
    public required string ContentType { get; set; }

    public int SizeBytes { get; set; }

    public int UploadedByUserId { get; set; }

    public DateTime UploadedAtUtc { get; set; }

    public GatePass? GatePass { get; set; }

    public GatePassAttachmentData? Data { get; set; }
}

/// <summary>The bytes of a photo. Stored in the database so backup and restore carry them.</summary>
public sealed class GatePassAttachmentData
{
    public int AttachmentId { get; set; }

    public int TenantId { get; set; } = 1;

    public required byte[] Data { get; set; }

    public GatePassAttachment? Attachment { get; set; }
}
