namespace HospitalPm.Infrastructure.Reports;

public sealed class ReportOptions
{
    public const string SectionName = "Reports";

    /// <summary>Printed as the document header. Set during install.</summary>
    public string HospitalName { get; set; } = string.Empty;

    /// <summary>Optional second header line — address, or department.</summary>
    public string? HospitalSubtitle { get; set; }

    /// <summary>
    /// Shown in the footer beside the accreditation reference an auditor
    /// looks for. Left blank until a hospital supplies theirs.
    /// </summary>
    public string? AccreditationReference { get; set; }
}

public sealed record CertificateLine(
    string SectionTitle,
    string Label,
    string Answer,
    string? Note,
    bool OutOfRange,
    string? Expected);

/// <summary>
/// Everything a PM certificate prints. Assembled by the endpoint so the
/// document itself does no data access — it is easier to reason about a
/// renderer that cannot surprise you with a query.
/// </summary>
public sealed record PmCertificateData(
    string AssetTag,
    string EquipmentTypeName,
    string LocationName,
    string? SerialNumber,
    string? Manufacturer,
    string? Model,
    string ChecklistName,
    int ChecklistVersionNo,
    DateTime? PerformedAtUtc,
    DateTime CompletedAtUtc,
    DateOnly DueDate,
    string TechnicianName,
    string? SignedByName,
    byte[]? Signature,
    string? SignatureFormat,
    string? Notes,
    IReadOnlyList<CertificateLine> Lines)
{
    public int OutOfRangeCount => Lines.Count(l => l.OutOfRange);

    /// <summary>
    /// A PM with a reading outside spec is not a clean pass, and a
    /// certificate that implies otherwise is worse than no certificate.
    /// </summary>
    public bool IsClean => OutOfRangeCount == 0;
}

public sealed record ServiceReportData(
    string Number,
    string AssetTag,
    string EquipmentTypeName,
    string LocationName,
    string? SerialNumber,
    string? Manufacturer,
    string? Model,
    string FaultDescription,
    string Priority,
    string Status,
    DateTime ReportedAtUtc,
    string ReportedByName,
    DateTime? StartedAtUtc,
    DateTime? ResolvedAtUtc,
    string? ResolvedByName,
    string? ResolutionNotes,
    DateTime? OutOfServiceAtUtc,
    DateTime? BackInServiceAtUtc,
    int? DowntimeMinutes,
    IReadOnlyList<(DateTime At, string Author, string Body)> Timeline);
