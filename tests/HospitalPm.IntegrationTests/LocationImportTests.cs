using ClosedXML.Excel;
using HospitalPm.Domain.Locations;
using HospitalPm.Infrastructure.Import;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public sealed class LocationImportTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_tree_imports_and_paths_are_built()
    {
        await using var db = fixture.CreateContext();
        var p = Prefix();

        var wb = Workbook(
            [$"{p}-SITE", "Main Campus", "Site", ""],
            [$"{p}-BLK", "Block A", "Building", $"{p}-SITE"],
            [$"{p}-ICU", "Intensive Care", "Department", $"{p}-BLK"]);

        var result = await new LocationImportService(db).CommitAsync(wb);

        Assert.True(result.Committed);
        Assert.Empty(result.Errors);

        // path and depth are computed by a database trigger, so the tracked
        // entities still hold what EF sent. Read untracked to see the row.
        var site = await db.Locations.AsNoTracking().SingleAsync(l => l.Code == $"{p}-SITE");
        var icu = await db.Locations.AsNoTracking().SingleAsync(l => l.Code == $"{p}-ICU");

        // The path trigger must have run for rows inserted by the importer,
        // or subtree search silently misses everything imported.
        Assert.StartsWith(site.Path, icu.Path, StringComparison.Ordinal);
        Assert.Equal(2, icu.Depth);
    }

    [Fact]
    public async Task A_parent_may_appear_after_its_children()
    {
        await using var db = fixture.CreateContext();
        var p = Prefix();

        // Hospitals export in whatever order their existing system produced.
        // Rejecting a file for row ordering would be a pointless obstacle.
        var wb = Workbook(
            [$"{p}-ROOM", "Bed 1", "Room", $"{p}-DEPT"],
            [$"{p}-DEPT", "Ward", "Department", ""]);

        var result = await new LocationImportService(db).CommitAsync(wb);

        Assert.True(result.Committed);

        var room = await db.Locations.AsNoTracking().SingleAsync(l => l.Code == $"{p}-ROOM");
        var dept = await db.Locations.AsNoTracking().SingleAsync(l => l.Code == $"{p}-DEPT");

        Assert.Equal(dept.Id, room.ParentId);
        Assert.StartsWith(dept.Path, room.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Levels_may_be_skipped()
    {
        await using var db = fixture.CreateContext();
        var p = Prefix();

        var wb = Workbook(
            [$"{p}-S", "Site", "Site", ""],
            [$"{p}-D", "Ward", "Department", $"{p}-S"]);

        var result = await new LocationImportService(db).CommitAsync(wb);

        Assert.True(result.Committed);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task An_inverted_level_is_rejected()
    {
        await using var db = fixture.CreateContext();
        var p = Prefix();

        var wb = Workbook(
            [$"{p}-R", "Bed", "Room", ""],
            [$"{p}-B", "Block", "Building", $"{p}-R"]);

        var result = await new LocationImportService(db).ValidateAsync(wb);

        Assert.Contains(result.Errors, e => e.Column == "Level");
    }

    [Fact]
    public async Task A_cycle_in_the_file_is_reported_with_codes()
    {
        await using var db = fixture.CreateContext();
        var p = Prefix();

        var wb = Workbook(
            [$"{p}-A", "A", "Building", $"{p}-B"],
            [$"{p}-B", "B", "Department", $"{p}-A"]);

        var result = await new LocationImportService(db).ValidateAsync(wb);

        // Caught in-file so the message names the codes the operator typed,
        // not internal ids from the path trigger.
        Assert.Contains(result.Errors, e =>
            e.Column == "Parent Code" && e.Message.Contains("loop", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_unknown_parent_is_reported()
    {
        await using var db = fixture.CreateContext();
        var p = Prefix();

        var wb = Workbook([$"{p}-X", "Orphan", "Room", $"{p}-NOPE"]);

        var result = await new LocationImportService(db).ValidateAsync(wb);

        Assert.Contains(result.Errors, e => e.Column == "Parent Code");
    }

    [Fact]
    public async Task A_code_already_in_the_database_is_rejected()
    {
        await using var db = fixture.CreateContext();
        var p = Prefix();

        db.Locations.Add(new Location { Code = $"{p}-DUP", Name = "Existing", Level = LocationLevel.Department });
        await db.SaveChangesAsync();

        var wb = Workbook([$"{p}-DUP", "Another", "Department", ""]);

        var result = await new LocationImportService(db).ValidateAsync(wb);

        Assert.Contains(result.Errors, e => e.Column == "Code");
    }

    [Fact]
    public async Task Nothing_is_written_when_any_row_is_invalid()
    {
        await using var db = fixture.CreateContext();
        var p = Prefix();

        var wb = Workbook(
            [$"{p}-OK", "Fine", "Department", ""],
            [$"{p}-BAD", "Broken", "NotALevel", ""]);

        var result = await new LocationImportService(db).CommitAsync(wb);

        Assert.False(result.Committed);
        Assert.False(await db.Locations.AnyAsync(l => l.Code == $"{p}-OK"));
    }

    [Fact]
    public async Task American_spelling_of_organisation_is_accepted()
    {
        await using var db = fixture.CreateContext();
        var p = Prefix();

        // Both spellings turn up in Indian hospital spreadsheets; accepting
        // only one would reject half of them for nothing.
        var wb = Workbook([$"{p}-ORG", "Group", "Organization", ""]);

        var result = await new LocationImportService(db).CommitAsync(wb);

        Assert.True(result.Committed);
        var org = await db.Locations.AsNoTracking().SingleAsync(l => l.Code == $"{p}-ORG");
        Assert.Equal(LocationLevel.Organisation, org.Level);
    }

    [Fact]
    public async Task Imported_locations_are_audited()
    {
        await using var db = fixture.CreateContext();
        var p = Prefix();

        await new LocationImportService(db).CommitAsync(Workbook([$"{p}-AUD", "Audited", "Department", ""]));

        var loc = await db.Locations.AsNoTracking().SingleAsync(l => l.Code == $"{p}-AUD");
        var conn = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE table_name = 'location' AND record_pk = @pk", conn);
        cmd.Parameters.AddWithValue("pk", loc.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(
            Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    [Fact]
    public void Template_carries_a_worked_example_that_skips_a_level()
    {
        using var ms = new MemoryStream(LocationImportService.BuildTemplate());
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheet(1);

        Assert.Equal("Code", ws.Cell(1, 1).GetString());
        Assert.Equal("Parent Code", ws.Cell(1, 4).GetString());
        // Department directly inside Building, with no Floor between them.
        Assert.Equal("Department", ws.Cell(4, 3).GetString());
        Assert.Equal("BLK-A", ws.Cell(4, 4).GetString());
    }

    private static string Prefix() => "L" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private static MemoryStream Workbook(params string[][] rows)
    {
        string[] headers = ["Code", "Name", "Level", "Parent Code"];

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
