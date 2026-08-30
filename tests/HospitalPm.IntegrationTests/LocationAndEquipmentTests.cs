using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Locations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HospitalPm.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public sealed class LocationAndEquipmentTests(PostgresFixture fixture)
{
    // ---------------- Location tree ----------------

    [Fact]
    public async Task Root_location_gets_a_path_and_zero_depth()
    {
        await using var db = fixture.CreateContext();

        var org = await NewLocationAsync(db, LocationLevel.Organisation, null);

        await db.Entry(org).ReloadAsync();

        Assert.Equal($"/{org.Id}/", org.Path);
        Assert.Equal(0, org.Depth);
    }

    [Fact]
    public async Task Child_path_contains_its_ancestors()
    {
        await using var db = fixture.CreateContext();

        var site = await NewLocationAsync(db, LocationLevel.Site, null);
        var building = await NewLocationAsync(db, LocationLevel.Building, site.Id);
        var room = await NewLocationAsync(db, LocationLevel.Room, building.Id);

        await db.Entry(room).ReloadAsync();

        Assert.Equal($"/{site.Id}/{building.Id}/{room.Id}/", room.Path);
        Assert.Equal(2, room.Depth);
    }

    [Fact]
    public async Task Levels_may_be_skipped()
    {
        await using var db = fixture.CreateContext();

        // A single-building nursing home has no Building or Floor worth
        // modelling. Site straight to Department must be legal.
        var site = await NewLocationAsync(db, LocationLevel.Site, null);
        var dept = await NewLocationAsync(db, LocationLevel.Department, site.Id);

        await db.Entry(dept).ReloadAsync();

        Assert.Equal($"/{site.Id}/{dept.Id}/", dept.Path);
    }

    [Fact]
    public async Task A_child_cannot_sit_at_a_shallower_level_than_its_parent()
    {
        await using var db = fixture.CreateContext();

        var room = await NewLocationAsync(db, LocationLevel.Room, null);

        // A Building under a Room inverts the hierarchy and would make
        // "everything under this site" meaningless.
        await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
            await NewLocationAsync(db, LocationLevel.Building, room.Id));
    }

    [Fact]
    public async Task A_child_cannot_sit_at_the_same_level_as_its_parent()
    {
        await using var db = fixture.CreateContext();

        var dept = await NewLocationAsync(db, LocationLevel.Department, null);

        await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
            await NewLocationAsync(db, LocationLevel.Department, dept.Id));
    }

    [Fact]
    public async Task Moving_a_subtree_rewrites_descendant_paths()
    {
        await using var db = fixture.CreateContext();

        var siteA = await NewLocationAsync(db, LocationLevel.Site, null);
        var siteB = await NewLocationAsync(db, LocationLevel.Site, null);
        var building = await NewLocationAsync(db, LocationLevel.Building, siteA.Id);
        var room = await NewLocationAsync(db, LocationLevel.Room, building.Id);

        // Re-parenting a building must carry its rooms with it. If the
        // descendants keep the old path they silently detach, and a search
        // for "everything under site B" misses them.
        building.ParentId = siteB.Id;
        await db.SaveChangesAsync();

        await db.Entry(building).ReloadAsync();
        await db.Entry(room).ReloadAsync();

        Assert.Equal($"/{siteB.Id}/{building.Id}/", building.Path);
        Assert.Equal($"/{siteB.Id}/{building.Id}/{room.Id}/", room.Path);
    }

    [Fact]
    public async Task Location_changes_are_audited()
    {
        await using var db = fixture.CreateContext();

        var loc = await NewLocationAsync(db, LocationLevel.Department, null);

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE table_name = 'location' AND record_pk = @pk", conn);
        cmd.Parameters.AddWithValue("pk", loc.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var rows = Convert.ToInt32(
            await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(rows > 0, "expected an audit row for the location insert");
    }

    // ---------------- Equipment ----------------

    [Fact]
    public async Task Equipment_can_be_placed_in_a_room()
    {
        await using var db = fixture.CreateContext();

        var room = await NewLocationAsync(db, LocationLevel.Room, null);
        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");

        var asset = new Equipment
        {
            AssetTag = "TAG-" + Guid.NewGuid().ToString("N")[..8],
            EquipmentTypeId = type.Id,
            LocationId = room.Id,
            Status = EquipmentStatus.InService,
        };
        db.Equipment.Add(asset);
        await db.SaveChangesAsync();

        Assert.True(asset.Id > 0);
    }

    [Fact]
    public async Task Equipment_cannot_be_parked_at_organisation_level()
    {
        await using var db = fixture.CreateContext();

        var org = await NewLocationAsync(db, LocationLevel.Organisation, null);
        var type = await db.EquipmentTypes.FirstAsync();

        // A technician dispatched to "the organisation" has been told
        // nothing. Building level or deeper is the floor.
        db.Equipment.Add(new Equipment
        {
            AssetTag = "ORG-" + Guid.NewGuid().ToString("N")[..8],
            EquipmentTypeId = type.Id,
            LocationId = org.Id,
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Asset_tag_is_unique_per_tenant()
    {
        await using var db = fixture.CreateContext();

        var room = await NewLocationAsync(db, LocationLevel.Room, null);
        var type = await db.EquipmentTypes.FirstAsync();
        var tag = "DUP-" + Guid.NewGuid().ToString("N")[..8];

        db.Equipment.Add(new Equipment { AssetTag = tag, EquipmentTypeId = type.Id, LocationId = room.Id });
        await db.SaveChangesAsync();

        // Two machines sharing a tag makes a QR scan ambiguous at the bedside.
        db.Equipment.Add(new Equipment { AssetTag = tag, EquipmentTypeId = type.Id, LocationId = room.Id });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Duplicate_serial_numbers_are_allowed()
    {
        await using var db = fixture.CreateContext();

        var room = await NewLocationAsync(db, LocationLevel.Room, null);
        var type = await db.EquipmentTypes.FirstAsync();

        // Hospitals hand over spreadsheets with blank and duplicated serials.
        // Rejecting them would reject the customer's real data on day one.
        db.Equipment.AddRange(
            new Equipment { AssetTag = "S1-" + Guid.NewGuid().ToString("N")[..8], SerialNumber = "SAME", EquipmentTypeId = type.Id, LocationId = room.Id },
            new Equipment { AssetTag = "S2-" + Guid.NewGuid().ToString("N")[..8], SerialNumber = "SAME", EquipmentTypeId = type.Id, LocationId = room.Id });

        await db.SaveChangesAsync();

        Assert.Equal(2, await db.Equipment.CountAsync(e => e.SerialNumber == "SAME"));
    }

    [Fact]
    public async Task Subtree_search_finds_equipment_under_an_ancestor()
    {
        await using var db = fixture.CreateContext();

        var site = await NewLocationAsync(db, LocationLevel.Site, null);
        var building = await NewLocationAsync(db, LocationLevel.Building, site.Id);
        var room = await NewLocationAsync(db, LocationLevel.Room, building.Id);
        var type = await db.EquipmentTypes.FirstAsync();

        var tag = "SUB-" + Guid.NewGuid().ToString("N")[..8];
        db.Equipment.Add(new Equipment { AssetTag = tag, EquipmentTypeId = type.Id, LocationId = room.Id });
        await db.SaveChangesAsync();

        await db.Entry(site).ReloadAsync();

        // Asking for a site must return assets in rooms three levels down.
        var found = await db.Equipment
            .Where(e => e.Location!.Path.StartsWith(site.Path))
            .Select(e => e.AssetTag)
            .ToListAsync();

        Assert.Contains(tag, found);
    }

    [Fact]
    public async Task A_location_holding_equipment_cannot_be_deleted()
    {
        await using var db = fixture.CreateContext();

        var room = await NewLocationAsync(db, LocationLevel.Room, null);
        var type = await db.EquipmentTypes.FirstAsync();

        db.Equipment.Add(new Equipment
        {
            AssetTag = "KEEP-" + Guid.NewGuid().ToString("N")[..8],
            EquipmentTypeId = type.Id,
            LocationId = room.Id,
        });
        await db.SaveChangesAsync();

        // Restrict, not Cascade: deleting a room must not silently delete
        // the register entries for the machines in it.
        //
        // Issued as raw SQL deliberately. Through the change tracker EF
        // severs the relationship client-side and throws before the database
        // is ever asked, which would test EF's behaviour rather than the
        // constraint. An Excel import or an admin running SQL bypasses EF
        // entirely, so the database is where this has to hold.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM location WHERE id = {0}", room.Id));

        Assert.Equal("23503", ex.SqlState); // foreign_key_violation
    }

    [Fact]
    public async Task Equipment_table_holds_no_patient_columns()
    {
        await using var db = fixture.CreateContext();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        // The constraint that removes an entire compliance burden. Asserted
        // against the live catalogue so a future column named patient_id or
        // mrn fails here rather than in a hospital's VAPT.
        const string sql = @"
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'equipment'
              AND (column_name LIKE '%patient%'
                OR column_name LIKE '%mrn%'
                OR column_name LIKE '%diagnos%'
                OR column_name = 'uhid')";

        await using var cmd = new NpgsqlCommand(sql, conn);
        var offenders = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            offenders.Add(reader.GetString(0));
        }

        Assert.Empty(offenders);
    }

    private static async Task<Location> NewLocationAsync(
        Infrastructure.Persistence.HospitalPmDbContext db, LocationLevel level, int? parentId)
    {
        var loc = new Location
        {
            Code = "L-" + Guid.NewGuid().ToString("N")[..10],
            Name = level + " " + Guid.NewGuid().ToString("N")[..4],
            Level = level,
            ParentId = parentId,
        };

        db.Locations.Add(loc);
        await db.SaveChangesAsync();
        return loc;
    }
}
