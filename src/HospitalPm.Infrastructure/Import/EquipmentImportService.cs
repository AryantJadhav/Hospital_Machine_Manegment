using System.Globalization;
using ClosedXML.Excel;
using HospitalPm.Domain.Assets;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Infrastructure.Import;

/// <summary>
/// Imports a hospital's asset spreadsheet.
///
/// Every hospital's first move is handing over a messy asset list, so this
/// is a sales surface, not a chore. Two properties matter more than feature
/// count:
///
///   1. Nothing is written until the whole file is known to be good. A
///      half-imported register is far worse to recover from than a rejected
///      file, because the retry then collides with its own partial rows.
///   2. Unmatched locations and types are reported, never invented. Silently
///      creating a location from a typo is how a register ends up with
///      fourteen spellings of "ICU" and a compliance report nobody trusts.
/// </summary>
public sealed class EquipmentImportService(HospitalPmDbContext db)
{
    /// <summary>
    /// Bounded so a mis-selected file cannot exhaust memory on a hospital PC.
    /// 20,000 rows is comfortably above the 15,000-asset target.
    /// </summary>
    public const int MaxRows = 20_000;

    private static readonly string[] Headers =
    [
        "Asset Tag",
        "Serial Number",
        "Equipment Type",
        "Location",
        "Manufacturer",
        "Model",
        "Status",
        "Purchase Date",
        "Installation Date",
        "Warranty Expiry",
        "Notes",
    ];

    /// <summary>
    /// The columns a file must actually contain. Serial number, dates and
    /// the rest are optional — serials in particular are routinely blank in
    /// a hospital's own register, so demanding the column would be asking
    /// for a header they have no data for.
    /// </summary>
    private static readonly string[] RequiredHeaders =
    [
        "Asset Tag",
        "Equipment Type",
        "Location",
    ];

    /// <summary>Parses and validates without writing anything.</summary>
    public async Task<ImportResult> ValidateAsync(Stream workbook, CancellationToken ct = default)
        => await RunAsync(workbook, commit: false, ct);

    /// <summary>
    /// Validates and, only if the file is entirely clean, writes it in one
    /// transaction.
    /// </summary>
    public async Task<ImportResult> CommitAsync(Stream workbook, CancellationToken ct = default)
        => await RunAsync(workbook, commit: true, ct);

    private async Task<ImportResult> RunAsync(Stream workbook, bool commit, CancellationToken ct)
    {
        var errors = new List<ImportError>();
        var rows = ParseRows(workbook, errors);

        if (rows.Count == 0)
        {
            return new ImportResult(0, 0, errors, false);
        }

        // Resolve references in bulk. Doing it per row would issue 15,000
        // round trips and turn a 20-second import into a 10-minute one.
        //
        // Loaded and grouped in memory rather than in SQL: these are lookup
        // tables of a few hundred rows at most, and EF cannot translate
        // GroupBy(...).First() so the query would throw at runtime.
        var allTypes = await db.EquipmentTypes
            .Select(t => new { t.Id, t.Code, t.Name })
            .ToListAsync(ct);

        var typesByCode = allTypes.ToDictionary(t => t.Code, t => t.Id, StringComparer.OrdinalIgnoreCase);

        // Ambiguous names are excluded rather than resolved arbitrarily. If
        // two types share a name, guessing which one the hospital meant is
        // worse than telling them to use the code.
        var typesByName = allTypes
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

        var allLocations = await db.Locations
            .Select(l => new { l.Id, l.Code, l.Name, l.Level })
            .ToListAsync(ct);

        var locationsByCode = allLocations
            .GroupBy(l => l.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (g.First().Id, g.First().Level), StringComparer.OrdinalIgnoreCase);

        var locationsByName = allLocations
            .GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => (g.First().Id, g.First().Level), StringComparer.OrdinalIgnoreCase);

        var existingTagSet = (await db.Equipment.Select(e => e.AssetTag).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var seenTags = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<Equipment>();

        foreach (var row in rows)
        {
            var rowErrors = errors.Count;

            if (string.IsNullOrWhiteSpace(row.AssetTag))
            {
                errors.Add(new ImportError(row.RowNumber, "Asset Tag", "Asset tag is required."));
            }
            else if (seenTags.TryGetValue(row.AssetTag, out var firstRow))
            {
                // Caught here rather than at the unique index, so the message
                // can name the other row instead of surfacing a constraint name.
                errors.Add(new ImportError(
                    row.RowNumber, "Asset Tag",
                    $"Duplicate asset tag '{row.AssetTag}', already used on row {firstRow} of this file."));
            }
            else if (existingTagSet.Contains(row.AssetTag))
            {
                errors.Add(new ImportError(
                    row.RowNumber, "Asset Tag",
                    $"Asset tag '{row.AssetTag}' already exists in the register."));
            }
            else
            {
                seenTags[row.AssetTag] = row.RowNumber;
            }

            var typeId = Resolve(row.TypeRef, typesByCode, typesByName);
            if (typeId is null)
            {
                errors.Add(new ImportError(
                    row.RowNumber, "Equipment Type",
                    string.IsNullOrWhiteSpace(row.TypeRef)
                        ? "Equipment type is required."
                        : $"No equipment type matches '{row.TypeRef}'. Add it first, or correct the spelling."));
            }

            int? locationId = null;
            var key = row.LocationRef?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(key))
            {
                errors.Add(new ImportError(row.RowNumber, "Location", "Location is required."));
            }
            else if (locationsByCode.TryGetValue(key, out var byCode))
            {
                locationId = byCode.Item1;
                if ((int)byCode.Item2 < (int)Domain.Locations.LocationLevel.Building)
                {
                    errors.Add(new ImportError(
                        row.RowNumber, "Location",
                        $"'{row.LocationRef}' is an organisation or site. Equipment must sit in a building, department or room."));
                }
            }
            else if (locationsByName.TryGetValue(key, out var byName))
            {
                locationId = byName.Item1;
                if ((int)byName.Item2 < (int)Domain.Locations.LocationLevel.Building)
                {
                    errors.Add(new ImportError(
                        row.RowNumber, "Location",
                        $"'{row.LocationRef}' is an organisation or site. Equipment must sit in a building, department or room."));
                }
            }
            else
            {
                // Deliberately not auto-created. See the class comment.
                errors.Add(new ImportError(
                    row.RowNumber, "Location",
                    $"No location matches '{row.LocationRef}'. Create it first so the register does not accumulate near-duplicate names."));
            }

            var status = ParseStatus(row.Status);
            if (row.Status is not null && status is null)
            {
                errors.Add(new ImportError(
                    row.RowNumber, "Status",
                    $"'{row.Status}' is not a known status. Use one of: {string.Join(", ", Enum.GetNames<EquipmentStatus>())}."));
            }

            if (errors.Count != rowErrors)
            {
                continue;
            }

            resolved.Add(new Equipment
            {
                AssetTag = row.AssetTag.Trim(),
                SerialNumber = Blank(row.SerialNumber),
                EquipmentTypeId = typeId!.Value,
                LocationId = locationId!.Value,
                Manufacturer = Blank(row.Manufacturer),
                Model = Blank(row.Model),
                Status = status ?? EquipmentStatus.InService,
                PurchaseDate = row.PurchaseDate,
                InstallationDate = row.InstallationDate,
                WarrantyExpiryDate = row.WarrantyExpiryDate,
                Notes = Blank(row.Notes),
            });
        }

        if (!commit || errors.Count > 0)
        {
            return new ImportResult(rows.Count, resolved.Count, errors, false);
        }

        // One transaction. Either the hospital's whole list lands or none of
        // it does; there is no state where half the register is imported and
        // a retry duplicates the other half.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Equipment.AddRange(resolved);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return new ImportResult(rows.Count, resolved.Count, errors, true);
    }

    private static List<ImportRow> ParseRows(Stream stream, List<ImportError> errors)
    {
        var rows = new List<ImportRow>();

        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.FirstOrDefault();

        if (ws is null)
        {
            errors.Add(new ImportError(0, "File", "The workbook has no worksheets."));
            return rows;
        }

        var used = ws.RangeUsed();
        if (used is null)
        {
            errors.Add(new ImportError(0, "File", "The worksheet is empty."));
            return rows;
        }

        // Header row is matched by name, not position, so a hospital that
        // reorders columns still imports.
        var headerRow = used.FirstRow();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var cell in headerRow.Cells())
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
            errors.Add(new ImportError(
                headerRow.RowNumber(), "Header",
                $"Missing required column(s): {string.Join(", ", missing)}."));
            return rows;
        }

        var dataRows = used.Rows().Skip(1).ToList();

        if (dataRows.Count > MaxRows)
        {
            errors.Add(new ImportError(
                0, "File",
                $"The file has {dataRows.Count} rows; the limit is {MaxRows}. Split it and import in parts."));
            return rows;
        }

        foreach (var row in dataRows)
        {
            if (row.IsEmpty())
            {
                continue;
            }

            rows.Add(new ImportRow(
                row.RowNumber(),
                Get(row, index, "Asset Tag"),
                Get(row, index, "Serial Number"),
                Get(row, index, "Equipment Type"),
                Get(row, index, "Location"),
                Get(row, index, "Manufacturer"),
                Get(row, index, "Model"),
                Blank(Get(row, index, "Status")),
                ParseDate(row, index, "Purchase Date"),
                ParseDate(row, index, "Installation Date"),
                ParseDate(row, index, "Warranty Expiry"),
                Get(row, index, "Notes")));
        }

        return rows;
    }

    private static string Get(IXLRangeRow row, Dictionary<string, int> index, string column)
        => index.TryGetValue(column, out var col) ? row.Cell(col).GetString().Trim() : string.Empty;

    private static DateOnly? ParseDate(IXLRangeRow row, Dictionary<string, int> index, string column)
    {
        if (!index.TryGetValue(column, out var col))
        {
            return null;
        }

        var cell = row.Cell(col);
        if (cell.IsEmpty())
        {
            return null;
        }

        if (cell.TryGetValue<DateTime>(out var dt))
        {
            return DateOnly.FromDateTime(dt);
        }

        // Text dates are the norm in hospital spreadsheets. Day-first is
        // parsed explicitly because Indian sheets are overwhelmingly
        // dd/MM/yyyy, and the invariant parser would read 03/04/2026 as
        // 4 March instead of 3 April.
        var text = cell.GetString().Trim();
        string[] formats = ["dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "yyyy-MM-dd", "dd.MM.yyyy"];

        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? DateOnly.FromDateTime(parsed)
            : null;
    }

    private static int? Resolve(
        string? reference,
        Dictionary<string, int> byCode,
        Dictionary<string, int> byName)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var key = reference.Trim();

        if (byCode.TryGetValue(key, out var id))
        {
            return id;
        }

        return byName.TryGetValue(key, out id) ? id : null;
    }

    private static EquipmentStatus? ParseStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = value.Replace(" ", string.Empty, StringComparison.Ordinal)
                           .Replace("-", string.Empty, StringComparison.Ordinal);

        return Enum.TryParse<EquipmentStatus>(cleaned, ignoreCase: true, out var parsed) ? parsed : null;
    }

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Builds the blank template. Column widths are set explicitly rather
    /// than with AdjustToContents: that path measures text through
    /// SixLabors.Fonts, which needs system fonts, and a minimal Linux
    /// container has none.
    /// </summary>
    public static byte[] BuildTemplate()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Equipment");

        for (var i = 0; i < Headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = Headers[i];
            cell.Style.Font.Bold = true;
            ws.Column(i + 1).Width = Headers[i].Length + 6;
        }

        ws.Cell(2, 1).Value = "BME-0001";
        ws.Cell(2, 3).Value = "Ventilator";
        ws.Cell(2, 4).Value = "ICU";
        ws.Cell(2, 7).Value = "InService";
        ws.Cell(2, 8).Value = "01/04/2024";

        ws.SheetView.FreezeRows(1);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}
