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
    string? Expected,
    bool Failed = false);

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

    public int FailedCount => Lines.Count(l => l.Failed);

    /// <summary>
    /// A PM with a reading outside spec, or a check that failed, is not a clean
    /// pass, and a certificate that implies otherwise is worse than no
    /// certificate.
    ///
    /// It used to look only at readings. A PM where every number was in range
    /// but the alarm test failed came out headed "All checks within
    /// specification" - the one thing this document must never say.
    /// </summary>
    public bool IsClean => OutOfRangeCount == 0 && FailedCount == 0;

    /// <summary>
    /// The headline an auditor reads first. Failures lead, because a failed
    /// check is the more serious finding.
    /// </summary>
    public string Verdict
    {
        get
        {
            if (IsClean) return "All checks within specification";

            var parts = new List<string>();
            if (FailedCount > 0)
            {
                parts.Add(FailedCount == 1 ? "1 check failed" : $"{FailedCount} checks failed");
            }

            if (OutOfRangeCount > 0)
            {
                parts.Add($"{OutOfRangeCount} reading(s) outside specification");
            }

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>A spare drawn against the repair, as the service report itemises it.</summary>
public sealed record ServiceReportPart(
    string PartNumber,
    string Name,
    int QuantityUsed,
    decimal? UnitCostAtUse)
{
    public decimal? LineTotal => UnitCostAtUse is { } cost ? cost * QuantityUsed : null;
}

/// <summary>A photo of the fault or the repair, printed alongside the record it belongs to.</summary>
public sealed record ServiceReportPhoto(string FileName, byte[] Data);

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
    IReadOnlyList<(DateTime At, string Author, string Body)> Timeline,
    IReadOnlyList<ServiceReportPart> PartsUsed,
    IReadOnlyList<ServiceReportPhoto> Photos,
    // False for a person from another department: which parts were used is printed, what they cost is not.
    bool ShowCosts = true)
{
    public decimal? PartsTotal => PartsUsed.Count == 0
        ? null
        : PartsUsed.Aggregate((decimal?)0m, (sum, p) => sum is null || p.LineTotal is null ? null : sum + p.LineTotal);
}
