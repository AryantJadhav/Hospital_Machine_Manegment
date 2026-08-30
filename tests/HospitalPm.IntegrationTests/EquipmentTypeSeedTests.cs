using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HospitalPm.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public sealed class EquipmentTypeSeedTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Seeded_types_are_present_and_marked_as_seeded()
    {
        await using var db = fixture.CreateContext();

        var seeded = await db.EquipmentTypes.CountAsync(t => t.IsSeeded);

        Assert.True(seeded > 100, $"expected the shipped starter set, found {seeded}");
    }

    [Fact]
    public async Task Every_seeded_type_has_exactly_one_primary_category()
    {
        await using var db = fixture.CreateContext();

        // "At most one" is enforced by a partial unique index. "At least one"
        // cannot be a row constraint, so it is asserted here — a seeded type
        // with no primary would silently vanish from default reporting.
        var offenders = await db.EquipmentTypes
            .Where(t => t.IsSeeded)
            .Select(t => new
            {
                t.Code,
                Primaries = t.Categories.Count(c => c.IsPrimary),
            })
            .Where(x => x.Primaries != 1)
            .ToListAsync();

        Assert.True(
            offenders.Count == 0,
            "types without exactly one primary category: " +
            string.Join(", ", offenders.Select(o => $"{o.Code}={o.Primaries}")));
    }

    [Fact]
    public async Task Overlapping_devices_carry_their_secondary_categories()
    {
        await using var db = fixture.CreateContext();

        // The case that motivated the many-to-many model in the first place.
        var ultrasound = await db.EquipmentTypes
            .Include(t => t.Categories).ThenInclude(l => l.Category)
            .SingleAsync(t => t.Code == "ultrasound-scanner");

        var codes = ultrasound.Categories.Select(l => l.Category!.Code).ToList();

        Assert.Contains("imaging", codes);
        Assert.Contains("diagnostic", codes);
        Assert.Equal("imaging", ultrasound.Categories.Single(l => l.IsPrimary).Category!.Code);
    }

    [Fact]
    public async Task Neonatal_ventilator_spans_all_three_of_its_categories()
    {
        await using var db = fixture.CreateContext();

        var type = await db.EquipmentTypes
            .Include(t => t.Categories).ThenInclude(l => l.Category)
            .SingleAsync(t => t.Code == "neonatal-ventilator");

        var codes = type.Categories.Select(l => l.Category!.Code).ToList();

        Assert.Contains("neonatal", codes);
        Assert.Contains("life-support", codes);
        Assert.Contains("therapeutic", codes);
    }

    [Fact]
    public async Task No_type_is_seeded_under_implantable()
    {
        await using var db = fixture.CreateContext();

        // Implants are placed inside a patient, receive no preventive
        // maintenance, and could only be tracked individually by recording
        // which patient has one. That would be patient data, which this
        // product does not hold. The category exists; it stays empty.
        var implantable = await db.Categories.SingleAsync(c => c.Code == "implantable");

        var linked = await db.EquipmentTypeCategories
            .CountAsync(l => l.CategoryId == implantable.Id);

        Assert.Equal(0, linked);
    }

    [Fact]
    public async Task Prosthetic_orthotic_seeds_workshop_equipment_not_fitted_items()
    {
        await using var db = fixture.CreateContext();

        var category = await db.Categories.SingleAsync(c => c.Code == "prosthetic-orthotic");

        var names = await db.EquipmentTypeCategories
            .Where(l => l.CategoryId == category.Id)
            .Select(l => l.EquipmentType!.Code)
            .ToListAsync();

        Assert.NotEmpty(names);
        Assert.All(names, n => Assert.StartsWith("po-", n, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Seed_is_idempotent_when_migrations_are_reapplied()
    {
        await using var db = fixture.CreateContext();
        var typesBefore = await db.EquipmentTypes.CountAsync();
        var linksBefore = await db.EquipmentTypeCategories.CountAsync();

        // Re-running a representative slice of the seed must be a no-op.
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO equipment_type (tenant_id, code, name, is_seeded, is_active) " +
            "VALUES (1, 'ultrasound-scanner', 'Ultrasound scanner', true, true) " +
            "ON CONFLICT (tenant_id, code) DO NOTHING;");

        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO equipment_type_category (tenant_id, equipment_type_id, category_id, is_primary) " +
            "SELECT 1, et.id, c.id, true FROM equipment_type et, category c " +
            "WHERE et.code = 'ultrasound-scanner' AND c.code = 'imaging' " +
            "ON CONFLICT (equipment_type_id, category_id) DO NOTHING;");

        Assert.Equal(typesBefore, await db.EquipmentTypes.CountAsync());
        Assert.Equal(linksBefore, await db.EquipmentTypeCategories.CountAsync());
    }

    [Fact]
    public async Task Seeding_wrote_audit_rows_via_triggers()
    {
        await using var db = fixture.CreateContext();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        // The seed runs as plain SQL inside a migration and never touches
        // audit_log. Rows for it exist only because triggers fire on
        // migration inserts too, which is what makes the trail complete
        // rather than "complete except for anything we shipped".
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE table_name = 'equipment_type' AND operation = 'INSERT'",
            conn);

        var count = Convert.ToInt32(
            await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(count > 100, $"expected audit rows for the seeded types, found {count}");
    }
}
