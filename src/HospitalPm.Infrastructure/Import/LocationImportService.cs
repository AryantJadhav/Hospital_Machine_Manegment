using ClosedXML.Excel;
using HospitalPm.Domain.Locations;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Infrastructure.Import;

/// <summary>
/// Bulk-loads the location tree from a spreadsheet.
///
/// Equipment import requires locations to already exist and deliberately
/// refuses to invent them, so without this a hospital would be typing two
/// hundred rooms in by hand before they could load a single asset.
///
/// Rows reference their parent by code, and a parent may appear later in the
/// file than its children. Hospitals export these in whatever order their
/// existing system produced, and rejecting a file for row ordering would be
/// a pointless obstacle — so the whole file is resolved as a graph before
/// anything is written.
/// </summary>
public sealed class LocationImportService(HospitalPmDbContext db)
{
    public const int MaxRows = 5_000;

    private static readonly string[] RequiredHeaders = ["Code", "Name", "Level"];

    public async Task<ImportResult> ValidateAsync(Stream workbook, CancellationToken ct = default)
        => await RunAsync(workbook, commit: false, ct);

    public async Task<ImportResult> CommitAsync(Stream workbook, CancellationToken ct = default)
        => await RunAsync(workbook, commit: true, ct);

    private async Task<ImportResult> RunAsync(Stream workbook, bool commit, CancellationToken ct)
    {
        var errors = new List<ImportError>();
        var rows = Parse(workbook, errors);

        if (rows.Count == 0)
        {
            return new ImportResult(0, 0, errors, false);
        }

        var existing = await db.Locations
            .Select(l => new { l.Id, l.Code, l.Level })
            .ToListAsync(ct);

        var existingByCode = existing.ToDictionary(
            l => l.Code, l => (l.Id, l.Level), StringComparer.OrdinalIgnoreCase);

        var inFile = new Dictionary<string, LocationRow>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.Code))
            {
                errors.Add(new ImportError(row.RowNumber, "Code", "Code is required."));
                continue;
            }

            if (inFile.TryGetValue(row.Code, out var first))
            {
                errors.Add(new ImportError(
                    row.RowNumber, "Code",
                    $"Duplicate code '{row.Code}', already used on row {first.RowNumber} of this file."));
                continue;
            }

            if (existingByCode.ContainsKey(row.Code))
            {
                errors.Add(new ImportError(
                    row.RowNumber, "Code",
                    $"Code '{row.Code}' already exists. Remove the row, or edit that location directly."));
                continue;
            }

            inFile[row.Code] = row;
        }

        foreach (var row in inFile.Values)
        {
            if (string.IsNullOrWhiteSpace(row.Name))
            {
                errors.Add(new ImportError(row.RowNumber, "Name", "Name is required."));
            }

            if (row.Level is null)
            {
                errors.Add(new ImportError(
                    row.RowNumber, "Level",
                    $"'{row.LevelText}' is not a known level. Use one of: {string.Join(", ", Enum.GetNames<LocationLevel>())}."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.ParentCode))
            {
                continue;
            }

            // Parent may be in this file or already in the database.
            LocationLevel? parentLevel = null;
            if (inFile.TryGetValue(row.ParentCode, out var parentRow))
            {
                parentLevel = parentRow.Level;
            }
            else if (existingByCode.TryGetValue(row.ParentCode, out var known))
            {
                parentLevel = known.Level;
            }

            if (parentLevel is null)
            {
                errors.Add(new ImportError(
                    row.RowNumber, "Parent Code",
                    $"No location matches parent '{row.ParentCode}', either in this file or already loaded."));
                continue;
            }

            if ((int)row.Level <= (int)parentLevel)
            {
                errors.Add(new ImportError(
                    row.RowNumber, "Level",
                    $"A {row.Level} cannot sit inside a {parentLevel}. Levels may be skipped, but a child must be deeper."));
            }
        }

        DetectCycles(inFile, errors);

        var valid = inFile.Values.Count(r => r.Level is not null && !string.IsNullOrWhiteSpace(r.Name));

        if (!commit || errors.Count > 0)
        {
            return new ImportResult(rows.Count, errors.Count > 0 ? 0 : valid, errors, false);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Inserted parents-first so each row's parent id exists when the
        // path trigger reads it. Order within a level does not matter.
        var created = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in inFile.Values.OrderBy(r => (int)r.Level!.Value))
        {
            int? parentId = null;

            if (!string.IsNullOrWhiteSpace(row.ParentCode))
            {
                if (created.TryGetValue(row.ParentCode, out var newId))
                {
                    parentId = newId;
                }
                else if (existingByCode.TryGetValue(row.ParentCode, out var known))
                {
                    parentId = known.Id;
                }
            }

            var entity = new Location
            {
                Code = row.Code.Trim(),
                Name = row.Name.Trim(),
                Level = row.Level!.Value,
                ParentId = parentId,
            };

            db.Locations.Add(entity);
            await db.SaveChangesAsync(ct);

            created[entity.Code] = entity.Id;
        }

        await tx.CommitAsync(ct);

        return new ImportResult(rows.Count, created.Count, errors, true);
    }

    /// <summary>
    /// Walks each row's parent chain within the file. A cycle here would
    /// otherwise reach the database, where the path trigger rejects it with
    /// a message naming internal ids rather than the codes the operator typed.
    /// </summary>
    private static void DetectCycles(Dictionary<string, LocationRow> inFile, List<ImportError> errors)
    {
        foreach (var row in inFile.Values)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { row.Code };
            var cursor = row.ParentCode;

            while (!string.IsNullOrWhiteSpace(cursor) && inFile.TryGetValue(cursor, out var parent))
            {
                if (!seen.Add(parent.Code))
                {
                    errors.Add(new ImportError(
                        row.RowNumber, "Parent Code",
                        $"'{row.Code}' is inside its own parent chain. Locations cannot form a loop."));
                    break;
                }

                cursor = parent.ParentCode;
            }
        }
    }

    private static List<LocationRow> Parse(Stream stream, List<ImportError> errors)
    {
        var rows = new List<LocationRow>();

        using var wb = new XLWorkbook(stream);
        var ws = FindDataSheet(wb, RequiredHeaders);

        if (ws?.RangeUsed() is not { } used)
        {
            errors.Add(new ImportError(0, "File", "The worksheet is empty."));
            return rows;
        }

        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in used.FirstRow().Cells())
        {
            var name = cell.GetString().Trim();
            if (!string.IsNullOrEmpty(name) && !index.ContainsKey(name))
            {
                index[name] = cell.Address.ColumnNumber;
            }
        }

        var missing = RequiredHeaders.Where(h => !index.ContainsKey(h)).ToList();
        if (missing.Count > 0)
        {
            // See EquipmentImportService: header problems are file-level.
            errors.Add(new ImportError(
                0, "Header", $"Missing required column(s): {string.Join(", ", missing)}."));
            return rows;
        }

        var dataRows = used.Rows().Skip(1).ToList();
        if (dataRows.Count > MaxRows)
        {
            errors.Add(new ImportError(
                0, "File", $"The file has {dataRows.Count} rows; the limit is {MaxRows}."));
            return rows;
        }

        foreach (var row in dataRows)
        {
            if (row.IsEmpty())
            {
                continue;
            }

            var levelText = Get(row, index, "Level");

            rows.Add(new LocationRow(
                row.RowNumber(),
                Get(row, index, "Code"),
                Get(row, index, "Name"),
                levelText,
                ParseLevel(levelText),
                Get(row, index, "Parent Code")));
        }

        return rows;
    }

    private static LocationLevel? ParseLevel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = text.Replace(" ", string.Empty, StringComparison.Ordinal);

        // "Organization" is at least as common as "Organisation" in an
        // Indian hospital's own spreadsheet; accepting only one spelling
        // would reject half of them for nothing.
        if (cleaned.Equals("Organization", StringComparison.OrdinalIgnoreCase))
        {
            return LocationLevel.Organisation;
        }

        return Enum.TryParse<LocationLevel>(cleaned, ignoreCase: true, out var parsed) ? parsed : null;
    }


    /// <summary>
    /// The first worksheet that actually carries the required headers.
    ///
    /// Taking worksheet one on faith breaks the moment a file grows a second
    /// sheet — a summary, an operator's working notes, or the "How to fix"
    /// sheet this app itself appends to a rejected file. Matching on headers
    /// means a re-upload works whatever order the sheets end up in.
    /// </summary>
    private static IXLWorksheet? FindDataSheet(XLWorkbook wb, string[] required)
    {
        foreach (var sheet in wb.Worksheets)
        {
            var used = sheet.RangeUsed();
            if (used is null)
            {
                continue;
            }

            var headers = used.FirstRow().Cells()
                .Select(c => c.GetString().Trim())
                .Where(h => h.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (required.All(headers.Contains))
            {
                return sheet;
            }
        }

        // Nothing matched: fall back to the first sheet so the caller reports
        // the missing headers against real content rather than saying the
        // file is empty.
        return wb.Worksheets.FirstOrDefault();
    }

    private static string Get(IXLRangeRow row, Dictionary<string, int> index, string column)
        => index.TryGetValue(column, out var col) ? row.Cell(col).GetString().Trim() : string.Empty;

    /// <summary>
    /// Template. Column widths are explicit rather than AdjustToContents,
    /// which measures through SixLabors.Fonts and needs system fonts a
    /// minimal Linux container does not have.
    /// </summary>
    public static byte[] BuildTemplate()
    {
        string[] headers = ["Code", "Name", "Level", "Parent Code"];

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Locations");

        for (var i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
            ws.Column(i + 1).Width = headers[i].Length + 10;
        }

        // A worked example: a site, a building inside it, and a department
        // that skips the Floor level entirely.
        string[][] sample =
        [
            ["MAIN", "Main Campus", "Site", ""],
            ["BLK-A", "Block A", "Building", "MAIN"],
            ["ICU", "Intensive Care Unit", "Department", "BLK-A"],
            ["ICU-01", "ICU Bed 1", "Room", "ICU"],
        ];

        for (var r = 0; r < sample.Length; r++)
        {
            for (var c = 0; c < sample[r].Length; c++)
            {
                ws.Cell(r + 2, c + 1).Value = sample[r][c];
            }
        }

        ws.SheetView.FreezeRows(1);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private sealed record LocationRow(
        int RowNumber,
        string Code,
        string Name,
        string LevelText,
        LocationLevel? Level,
        string ParentCode);
}
