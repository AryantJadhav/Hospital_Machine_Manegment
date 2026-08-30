using ClosedXML.Excel;
using HospitalPm.Domain.Locations;
using HospitalPm.Infrastructure.Import;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public sealed class ExcelImportTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Clean_file_imports_every_row()
    {
        await using var db = fixture.CreateContext();
        var (dept, _) = await SeedLocationAsync(db);
        var tag1 = NewTag();
        var tag2 = NewTag();

        var wb = Workbook(
            [tag1, "SN-1", "Ventilator", dept, "Philips", "V60", "InService", "01/04/2024", "", "", ""],
            [tag2, "SN-2", "ECG machine", dept, "GE", "MAC800", "InStore", "", "", "", ""]);

        var result = await new EquipmentImportService(db).CommitAsync(wb);

        Assert.True(result.Committed);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.ValidRows);
        Assert.True(await db.Equipment.AnyAsync(e => e.AssetTag == tag1));
        Assert.True(await db.Equipment.AnyAsync(e => e.AssetTag == tag2));
    }

    [Fact]
    public async Task Nothing_is_written_when_any_row_is_invalid()
    {
        await using var db = fixture.CreateContext();
        var (dept, _) = await SeedLocationAsync(db);
        var good = NewTag();

        var wb = Workbook(
            [good, "", "Ventilator", dept, "", "", "", "", "", "", ""],
            [NewTag(), "", "Nonexistent Machine", dept, "", "", "", "", "", "", ""]);

        var result = await new EquipmentImportService(db).CommitAsync(wb);

        // All-or-nothing. A half-imported register is worse than a rejected
        // file, because the retry then collides with its own partial rows.
        Assert.False(result.Committed);
        Assert.NotEmpty(result.Errors);
        Assert.False(await db.Equipment.AnyAsync(e => e.AssetTag == good));
    }

    [Fact]
    public async Task Validate_writes_nothing_even_when_the_file_is_clean()
    {
        await using var db = fixture.CreateContext();
        var (dept, _) = await SeedLocationAsync(db);
        var tag = NewTag();

        var wb = Workbook([tag, "", "Ventilator", dept, "", "", "", "", "", "", ""]);

        var result = await new EquipmentImportService(db).ValidateAsync(wb);

        Assert.False(result.Committed);
        Assert.Empty(result.Errors);
        Assert.Equal(1, result.ValidRows);
        Assert.False(await db.Equipment.AnyAsync(e => e.AssetTag == tag));
    }

    [Fact]
    public async Task Unknown_equipment_type_is_reported_not_created()
    {
        await using var db = fixture.CreateContext();
        var (dept, _) = await SeedLocationAsync(db);
        var before = await db.EquipmentTypes.CountAsync();

        var wb = Workbook([NewTag(), "", "Flux Capacitor", dept, "", "", "", "", "", "", ""]);

        var result = await new EquipmentImportService(db).ValidateAsync(wb);

        Assert.Contains(result.Errors, e => e.Column == "Equipment Type");
        Assert.Equal(before, await db.EquipmentTypes.CountAsync());
    }

    [Fact]
    public async Task Unknown_location_is_reported_not_created()
    {
        await using var db = fixture.CreateContext();
        await SeedLocationAsync(db);
        var before = await db.Locations.CountAsync();

        var wb = Workbook([NewTag(), "", "Ventilator", "Ward That Does Not Exist", "", "", "", "", "", "", ""]);

        var result = await new EquipmentImportService(db).ValidateAsync(wb);

        // Auto-creating from a typo is how a register ends up with fourteen
        // spellings of "ICU" and a compliance report nobody trusts.
        Assert.Contains(result.Errors, e => e.Column == "Location");
        Assert.Equal(before, await db.Locations.CountAsync());
    }

    [Fact]
    public async Task Duplicate_tag_within_the_file_names_the_other_row()
    {
        await using var db = fixture.CreateContext();
        var (dept, _) = await SeedLocationAsync(db);
        var tag = NewTag();

        var wb = Workbook(
            [tag, "", "Ventilator", dept, "", "", "", "", "", "", ""],
            [tag, "", "Ventilator", dept, "", "", "", "", "", "", ""]);

        var result = await new EquipmentImportService(db).ValidateAsync(wb);

        var error = Assert.Single(result.Errors, e => e.Column == "Asset Tag");
        Assert.Contains("row 2", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tag_already_in_the_register_is_rejected()
    {
        await using var db = fixture.CreateContext();
        var (dept, deptId) = await SeedLocationAsync(db);
        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
        var tag = NewTag();

        db.Equipment.Add(new Domain.Assets.Equipment
        {
            AssetTag = tag,
            EquipmentTypeId = type.Id,
            LocationId = deptId,
        });
        await db.SaveChangesAsync();

        var wb = Workbook([tag, "", "Ventilator", dept, "", "", "", "", "", "", ""]);

        var result = await new EquipmentImportService(db).ValidateAsync(wb);

        Assert.Contains(result.Errors, e => e.Column == "Asset Tag" && e.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Blank_and_duplicate_serials_are_accepted()
    {
        await using var db = fixture.CreateContext();
        var (dept, _) = await SeedLocationAsync(db);

        // Hospitals hand over sheets full of these. Rejecting them would
        // reject the customer's real data on day one.
        var wb = Workbook(
            [NewTag(), "", "Ventilator", dept, "", "", "", "", "", "", ""],
            [NewTag(), "SAME-SERIAL", "Ventilator", dept, "", "", "", "", "", "", ""],
            [NewTag(), "SAME-SERIAL", "Ventilator", dept, "", "", "", "", "", "", ""]);

        var result = await new EquipmentImportService(db).CommitAsync(wb);

        Assert.True(result.Committed);
        Assert.Equal(3, result.ValidRows);
    }

    [Fact]
    public async Task Day_first_dates_are_read_correctly()
    {
        await using var db = fixture.CreateContext();
        var (dept, _) = await SeedLocationAsync(db);
        var tag = NewTag();

        // 03/04/2026 is 3 April in an Indian hospital's spreadsheet. Read as
        // month-first it silently becomes 4 March, and every warranty and PM
        // due date derived from it is a month out.
        var wb = Workbook([tag, "", "Ventilator", dept, "", "", "", "03/04/2026", "", "", ""]);

        await new EquipmentImportService(db).CommitAsync(wb);

        var saved = await db.Equipment.SingleAsync(e => e.AssetTag == tag);

        Assert.Equal(new DateOnly(2026, 4, 3), saved.PurchaseDate);
    }

    [Fact]
    public async Task Columns_may_be_reordered()
    {
        await using var db = fixture.CreateContext();
        var (dept, _) = await SeedLocationAsync(db);
        var tag = NewTag();

        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Sheet1");
            // Headers matched by name, not position, so a hospital that
            // rearranges its columns still imports.
            ws.Cell(1, 1).Value = "Location";
            ws.Cell(1, 2).Value = "Equipment Type";
            ws.Cell(1, 3).Value = "Asset Tag";
            ws.Cell(2, 1).Value = dept;
            ws.Cell(2, 2).Value = "Ventilator";
            ws.Cell(2, 3).Value = tag;
            wb.SaveAs(ms);
        }
        ms.Position = 0;

        var result = await new EquipmentImportService(db).CommitAsync(ms);

        Assert.True(result.Committed);
        Assert.True(await db.Equipment.AnyAsync(e => e.AssetTag == tag));
    }

    [Fact]
    public async Task Missing_required_header_is_reported_once()
    {
        await using var db = fixture.CreateContext();

        using var ms = new MemoryStream();
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Sheet1");
            ws.Cell(1, 1).Value = "Asset Tag";
            ws.Cell(1, 2).Value = "Serial Number";
            ws.Cell(2, 1).Value = "X-1";
            wb.SaveAs(ms);
        }
        ms.Position = 0;

        var result = await new EquipmentImportService(db).ValidateAsync(ms);

        // One clear message about the header beats one error per data row.
        var error = Assert.Single(result.Errors);
        Assert.Equal("Header", error.Column);
        Assert.Contains("Equipment Type", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Equipment_cannot_be_imported_onto_a_site()
    {
        await using var db = fixture.CreateContext();

        var site = new Location
        {
            Code = "SITE-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Site " + Guid.NewGuid().ToString("N")[..4],
            Level = LocationLevel.Site,
        };
        db.Locations.Add(site);
        await db.SaveChangesAsync();

        var wb = Workbook([NewTag(), "", "Ventilator", site.Code, "", "", "", "", "", "", ""]);

        var result = await new EquipmentImportService(db).ValidateAsync(wb);

        Assert.Contains(result.Errors, e => e.Column == "Location");
    }

    [Fact]
    public void Template_is_a_readable_workbook_with_the_expected_headers()
    {
        var bytes = EquipmentImportService.BuildTemplate();

        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheet(1);

        Assert.Equal("Asset Tag", ws.Cell(1, 1).GetString());
        Assert.Equal("Equipment Type", ws.Cell(1, 3).GetString());
        Assert.Equal("Location", ws.Cell(1, 4).GetString());
    }

    [Fact]
    public async Task Import_rows_are_audited()
    {
        await using var db = fixture.CreateContext();
        var (dept, _) = await SeedLocationAsync(db);
        var tag = NewTag();

        var wb = Workbook([tag, "", "Ventilator", dept, "", "", "", "", "", "", ""]);
        await new EquipmentImportService(db).CommitAsync(wb);

        var saved = await db.Equipment.SingleAsync(e => e.AssetTag == tag);
        var conn = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        // Bulk import must not be a hole in the audit trail.
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE table_name = 'equipment' AND record_pk = @pk", conn);
        cmd.Parameters.AddWithValue("pk", saved.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var rows = Convert.ToInt32(
            await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(rows > 0, "expected an audit row for the imported asset");
    }

    // ---------------- helpers ----------------

    private static string NewTag() => "IMP-" + Guid.NewGuid().ToString("N")[..10];

    private static async Task<(string Code, int Id)> SeedLocationAsync(HospitalPmDbContext db)
    {
        var dept = new Location
        {
            Code = "DEPT-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Dept " + Guid.NewGuid().ToString("N")[..4],
            Level = LocationLevel.Department,
        };
        db.Locations.Add(dept);
        await db.SaveChangesAsync();
        return (dept.Code, dept.Id);
    }

    private static MemoryStream Workbook(params string[][] rows)
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
            for (var c = 0; c < headers.Length; c++)
            {
                ws.Cell(1, c + 1).Value = headers[c];
            }

            for (var r = 0; r < rows.Length; r++)
            {
                for (var c = 0; c < rows[r].Length; c++)
                {
                    if (!string.IsNullOrEmpty(rows[r][c]))
                    {
                        // Written as text so date parsing is exercised the way
                        // a hand-typed hospital sheet actually stores it.
                        ws.Cell(r + 2, c + 1).SetValue(rows[r][c]);
                    }
                }
            }

            wb.SaveAs(ms);
        }

        ms.Position = 0;
        return ms;
    }
}
