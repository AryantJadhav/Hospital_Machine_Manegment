using ClosedXML.Excel;

namespace HospitalPm.Infrastructure.Import;

/// <summary>
/// Hands the hospital's own spreadsheet back with the problems written
/// against the rows that caused them.
///
/// A list of forty errors in a web page is close to useless when the file has
/// two thousand rows: the operator has to alt-tab, find row 1,417, fix it,
/// and repeat. Annotating their own file means they fix it in the place they
/// already work and re-upload the same document.
///
/// The annotated file is deliberately still importable. Problems are written
/// to an appended column, which the parser ignores because it matches headers
/// by name, and the summary goes on a sheet added at the end so it never
/// becomes the sheet the parser reads.
/// </summary>
public static class ImportErrorReport
{
    private const string ProblemColumn = "Problems";
    private const string SummarySheet = "How to fix";

    public static byte[] Annotate(Stream original, IReadOnlyList<ImportError> errors)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(errors);

        original.Position = 0;
        using var wb = new XLWorkbook(original);

        var ws = wb.Worksheets.First();
        var used = ws.RangeUsed();

        // Errors carrying row 0 are about the file as a whole — a missing
        // header, too many rows — and belong only in the summary.
        var byRow = errors
            .Where(e => e.RowNumber > 0)
            .GroupBy(e => e.RowNumber)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (used is not null)
        {
            var headerRow = used.FirstRow().RowNumber();
            var problemColumn = used.LastColumn().ColumnNumber() + 1;

            var header = ws.Cell(headerRow, problemColumn);
            header.Value = ProblemColumn;
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = XLColor.FromArgb(0xFF, 0xE0, 0xE0);
            ws.Column(problemColumn).Width = 60;

            foreach (var (rowNumber, rowErrors) in byRow)
            {
                // Guard: a problem reported against the header row would
                // overwrite the Problems header that was just written.
                if (rowNumber <= headerRow)
                {
                    continue;
                }

                var cell = ws.Cell(rowNumber, problemColumn);
                cell.Value = string.Join("  •  ", rowErrors.Select(e => $"{e.Column}: {e.Message}"));
                cell.Style.Alignment.WrapText = true;
                cell.Style.Font.FontColor = XLColor.FromArgb(0x99, 0x00, 0x00);

                // Tint the whole row: an operator scrolling a long sheet finds
                // the bad rows by colour, not by reading every cell.
                for (var c = 1; c <= problemColumn; c++)
                {
                    ws.Cell(rowNumber, c).Style.Fill.BackgroundColor = XLColor.FromArgb(0xFF, 0xF2, 0xF2);
                }
            }

            ws.SheetView.FreezeRows(headerRow);
        }

        // Appended, never inserted. The parser reads the first worksheet, so
        // putting this at the front would make a re-upload try to import the
        // summary.
        BuildSummary(wb, errors, byRow.Count);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void BuildSummary(XLWorkbook wb, IReadOnlyList<ImportError> errors, int affectedRows)
    {
        if (wb.Worksheets.Any(s => s.Name == SummarySheet))
        {
            wb.Worksheets.Delete(SummarySheet);
        }

        var ws = wb.AddWorksheet(SummarySheet);

        ws.Cell(1, 1).Value = "This file was not imported";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;

        ws.Cell(2, 1).Value =
            "Nothing was saved. Fix the rows marked in red on the first sheet, then upload this same file again.";

        ws.Cell(3, 1).Value =
            $"The '{ProblemColumn}' column is ignored on import, so you can leave it in place.";
        ws.Cell(3, 1).Style.Font.FontColor = XLColor.Gray;

        ws.Cell(5, 1).Value = "Rows with problems";
        ws.Cell(5, 2).Value = affectedRows;
        ws.Cell(6, 1).Value = "Problems in total";
        ws.Cell(6, 2).Value = errors.Count;
        ws.Range(5, 1, 6, 1).Style.Font.Bold = true;

        ws.Cell(8, 1).Value = "Row";
        ws.Cell(8, 2).Value = "Column";
        ws.Cell(8, 3).Value = "Problem";
        ws.Range(8, 1, 8, 3).Style.Font.Bold = true;
        ws.Range(8, 1, 8, 3).Style.Fill.BackgroundColor = XLColor.FromArgb(0xEE, 0xEE, 0xEE);

        var row = 9;
        foreach (var error in errors.OrderBy(e => e.RowNumber).ThenBy(e => e.Column, StringComparer.Ordinal))
        {
            // Row 0 means the problem is with the file, not a line in it.
            if (error.RowNumber > 0)
            {
                ws.Cell(row, 1).Value = error.RowNumber;
            }
            else
            {
                ws.Cell(row, 1).Value = "File";
            }
            ws.Cell(row, 2).Value = error.Column;
            ws.Cell(row, 3).Value = error.Message;
            row++;
        }

        // Explicit widths rather than AdjustToContents, which measures through
        // SixLabors.Fonts and needs system fonts a minimal Linux container
        // does not have.
        ws.Column(1).Width = 8;
        ws.Column(2).Width = 20;
        ws.Column(3).Width = 90;
        ws.Column(3).Style.Alignment.WrapText = true;

        ws.SheetView.FreezeRows(8);
    }
}
