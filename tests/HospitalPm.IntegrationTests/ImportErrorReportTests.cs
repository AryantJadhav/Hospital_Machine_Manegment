using ClosedXML.Excel;
using HospitalPm.Domain.Locations;
using HospitalPm.Infrastructure.Import;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public sealed class ImportErrorReportTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Problems_are_written_against_the_rows_that_caused_them()
    {
        await using var db = fixture.CreateContext();
        var dept = await SeedDeptAsync(db);

        var wb = EquipmentWorkbook(
            [NewTag(), "", "Ventilator", dept, "", "", "", "", "", "", ""],
            [NewTag(), "", "Flux Capacitor", dept, "", "", "", "", "", "", ""]);

        var result = await new EquipmentImportService(db).ValidateAsync(wb);
        var annotated = ImportErrorReport.Annotate(wb, result.Errors);

        using var ms = new MemoryStream(annotated);
        using var book = new XLWorkbook(ms);
        var ws = book.Worksheet(1);

        var problemColumn = ws.RangeUsed()!.LastColumn().ColumnNumber();
        Assert.Equal("Problems", ws.Cell(1, problemColumn).GetString());

        // Row 2 is fine; row 3 has the unknown type. The message must land on
        // row 3, because the whole point is fixing it in place.
        Assert.Equal(string.Empty, ws.Cell(2, problemColumn).GetString());
        Assert.Contains("Flux Capacitor", ws.Cell(3, problemColumn).GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_annotated_file_can_be_fixed_and_re_imported()
    {
        await using var db = fixture.CreateContext();
        var dept = await SeedDeptAsync(db);
        var good = NewTag();
        var bad = NewTag();

        var wb = EquipmentWorkbook(
            [good, "", "Ventilator", dept, "", "", "", "", "", "", ""],
            [bad, "", "Flux Capacitor", dept, "", "", "", "", "", "", ""]);

        var service = new EquipmentImportService(db);
        var first = await service.ValidateAsync(wb);
        Assert.NotEmpty(first.Errors);

        var annotated = ImportErrorReport.Annotate(wb, first.Errors);

        // The operator fixes the bad row in the file they were handed and
        // uploads that same file. The Problems column and the summary sheet
        // must not get in the way.
        using var fixedStream = new MemoryStream();
        using (var book = new XLWorkbook(new MemoryStream(annotated)))
        {
            var ws = book.Worksheet(1);
            ws.Cell(3, 3).Value = "Ventilator";
            book.SaveAs(fixedStream);
        }
        fixedStream.Position = 0;

        var second = await service.CommitAsync(fixedStream);

        Assert.True(second.Committed, string.Join("; ", second.Errors.Select(e => e.Message)));
        Assert.True(await db.Equipment.AnyAsync(e => e.AssetTag == good));
        Assert.True(await db.Equipment.AnyAsync(e => e.AssetTag == bad));
    }

    [Fact]
    public async Task The_summary_sheet_is_added_last_so_it_is_never_parsed()
    {
        await using var db = fixture.CreateContext();
        var dept = await SeedDeptAsync(db);

        var wb = EquipmentWorkbook([NewTag(), "", "Nope", dept, "", "", "", "", "", "", ""]);
        var result = await new EquipmentImportService(db).ValidateAsync(wb);

        using var book = new XLWorkbook(new MemoryStream(ImportErrorReport.Annotate(wb, result.Errors)));

        // Inserting it first would make a re-upload try to import the summary.
        Assert.Equal("How to fix", book.Worksheets.Last().Name);
        Assert.NotEqual("How to fix", book.Worksheets.First().Name);
    }

    [Fact]
    public async Task The_data_sheet_is_found_by_its_headers_not_its_position()
    {
        await using var db = fixture.CreateContext();
        var dept = await SeedDeptAsync(db);
        var tag = NewTag();

        using var ms = new MemoryStream();
        using (var book = new XLWorkbook())
        {
            // An operator's notes sheet sitting in front of the data. Taking
            // worksheet one on faith would read this and report an empty file.
            var notes = book.AddWorksheet("Notes");
            notes.Cell(1, 1).Value = "Asked biomedical about the ICU vents";

            var ws = book.AddWorksheet("Assets");
            string[] headers = ["Asset Tag", "Equipment Type", "Location"];
            for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            ws.Cell(2, 1).Value = tag;
            ws.Cell(2, 2).Value = "Ventilator";
            ws.Cell(2, 3).Value = dept;

            book.SaveAs(ms);
        }
        ms.Position = 0;

        var result = await new EquipmentImportService(db).CommitAsync(ms);

        Assert.True(result.Committed, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.True(await db.Equipment.AnyAsync(e => e.AssetTag == tag));
    }

    [Fact]
    public async Task A_file_level_problem_appears_in_the_summary()
    {
        await using var db = fixture.CreateContext();

        using var ms = new MemoryStream();
        using (var book = new XLWorkbook())
        {
            var ws = book.AddWorksheet("Sheet1");
            ws.Cell(1, 1).Value = "Asset Tag";
            ws.Cell(2, 1).Value = "X-1";
            book.SaveAs(ms);
        }
        ms.Position = 0;

        var result = await new EquipmentImportService(db).ValidateAsync(ms);
        var annotated = ImportErrorReport.Annotate(ms, result.Errors);

        using var book2 = new XLWorkbook(new MemoryStream(annotated));
        var summary = book2.Worksheet("How to fix");
        var text = summary.RangeUsed()!.Cells().Select(c => c.GetString()).ToList();

        // A missing header is not attached to any row, so it would vanish if
        // the summary only listed row-level problems.
        Assert.Contains(text, s => s.Contains("Equipment Type", StringComparison.Ordinal));
        Assert.Contains(text, s => s.Contains("File", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Location_files_are_annotated_too()
    {
        await using var db = fixture.CreateContext();
        var p = "LR" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        using var ms = new MemoryStream();
        using (var book = new XLWorkbook())
        {
            var ws = book.AddWorksheet("Sheet1");
            string[] headers = ["Code", "Name", "Level", "Parent Code"];
            for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];
            ws.Cell(2, 1).Value = $"{p}-A";
            ws.Cell(2, 2).Value = "Orphan";
            ws.Cell(2, 3).Value = "Room";
            ws.Cell(2, 4).Value = $"{p}-MISSING";
            book.SaveAs(ms);
        }
        ms.Position = 0;

        var result = await new LocationImportService(db).ValidateAsync(ms);
        Assert.NotEmpty(result.Errors);

        var annotated = ImportErrorReport.Annotate(ms, result.Errors);

        using var book2 = new XLWorkbook(new MemoryStream(annotated));
        var ws2 = book2.Worksheet(1);
        var problemColumn = ws2.RangeUsed()!.LastColumn().ColumnNumber();

        Assert.Contains("MISSING", ws2.Cell(2, problemColumn).GetString(), StringComparison.Ordinal);
    }

    // ---------------- helpers ----------------

    private static string NewTag() => "ERR-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    private static async Task<string> SeedDeptAsync(Infrastructure.Persistence.HospitalPmDbContext db)
    {
        var dept = new Location
        {
            Code = "ERD-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Dept " + Guid.NewGuid().ToString("N")[..4],
            Level = LocationLevel.Department,
        };
        db.Locations.Add(dept);
        await db.SaveChangesAsync();
        return dept.Code;
    }

    private static MemoryStream EquipmentWorkbook(params string[][] rows)
    {
        string[] headers =
        [
            "Asset Tag", "Serial Number", "Equipment Type", "Location", "Manufacturer",
            "Model", "Status", "Purchase Date", "Installation Date", "Warranty Expiry", "Notes",
        ];

        var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Sheet1");
            for (var c = 0; c < headers.Length; c++) ws.Cell(1, c + 1).Value = headers[c];

            for (var r = 0; r < rows.Length; r++)
            {
                for (var c = 0; c < rows[r].Length; c++)
                {
                    if (!string.IsNullOrEmpty(rows[r][c])) ws.Cell(r + 2, c + 1).SetValue(rows[r][c]);
                }
            }

            wb.SaveAs(ms);
        }

        ms.Position = 0;
        return ms;
    }
}
