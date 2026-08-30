namespace HospitalPm.Infrastructure.Import;

/// <summary>One problem with one cell, addressed the way the user sees it.</summary>
/// <param name="RowNumber">1-based worksheet row, matching what Excel shows in the gutter.</param>
public sealed record ImportError(int RowNumber, string Column, string Message);

/// <summary>
/// Outcome of parsing and validating a workbook.
///
/// Returned by the dry run and again by the commit, so the caller sees the
/// same shape either way.
/// </summary>
public sealed record ImportResult(
    int TotalRows,
    int ValidRows,
    IReadOnlyList<ImportError> Errors,
    bool Committed)
{
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>A parsed row, before it becomes an entity.</summary>
internal sealed record ImportRow(
    int RowNumber,
    string AssetTag,
    string? SerialNumber,
    string TypeRef,
    string LocationRef,
    string? Manufacturer,
    string? Model,
    string? Status,
    DateOnly? PurchaseDate,
    DateOnly? InstallationDate,
    DateOnly? WarrantyExpiryDate,
    string? Notes);
