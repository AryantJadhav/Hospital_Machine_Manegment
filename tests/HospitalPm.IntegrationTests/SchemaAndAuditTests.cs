using HospitalPm.Domain.Equipment;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HospitalPm.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public sealed class SchemaAndAuditTests(PostgresFixture fixture)
{
    // ---------------- Phase 0 gate: migrations ----------------

    [Fact]
    public async Task Migrations_bring_an_empty_database_to_current_in_one_step()
    {
        await using var db = fixture.CreateContext();

        var pending = await db.Database.GetPendingMigrationsAsync();

        Assert.Empty(pending);
    }

    [Fact]
    public async Task All_eighteen_categories_are_seeded()
    {
        await using var db = fixture.CreateContext();

        var count = await db.Categories.CountAsync();

        Assert.Equal(18, count);
    }

    [Fact]
    public async Task Category_seed_is_idempotent()
    {
        await using var db = fixture.CreateContext();
        var before = await db.Categories.CountAsync();

        // Re-running a seed statement must not duplicate rows. Migrations
        // are required to be idempotent, and the seed is the easiest part
        // to get wrong.
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO category (tenant_id, code, name, display_order, is_active) " +
            "VALUES (1, 'diagnostic', 'Diagnostic devices', 1, true) " +
            "ON CONFLICT (tenant_id, code) DO NOTHING;");

        Assert.Equal(before, await db.Categories.CountAsync());
    }

    [Fact]
    public async Task Every_table_carries_tenant_id()
    {
        await using var db = fixture.CreateContext();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        // tenant_id on every table is a project rule. Asserting it against
        // the live catalogue means a future table that forgets it fails here
        // rather than during a multi-site rollout years later.
        const string sql = @"
            SELECT t.table_name
            FROM information_schema.tables t
            WHERE t.table_schema = 'public'
              AND t.table_type = 'BASE TABLE'
              AND t.table_name <> '__EFMigrationsHistory'
              AND NOT EXISTS (
                    SELECT 1 FROM information_schema.columns c
                    WHERE c.table_schema = 'public'
                      AND c.table_name = t.table_name
                      AND c.column_name = 'tenant_id')";

        await using var cmd = new NpgsqlCommand(sql, conn);
        var missing = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            missing.Add(reader.GetString(0));
        }

        Assert.Empty(missing);
    }

    // ---------------- Phase 0 gate: trigger-written audit ----------------

    [Fact]
    public async Task Audit_row_appears_without_application_code_writing_it()
    {
        await using var db = fixture.CreateContext();

        // Nothing in this test touches audit_log. If a row appears, a
        // database trigger wrote it. This is the Phase 0 gate.
        var type = new EquipmentType
        {
            Code = "probe-" + Guid.NewGuid().ToString("N"),
            Name = "Audit probe",
        };
        db.EquipmentTypes.Add(type);
        await db.SaveChangesAsync();

        var inserts = await CountAuditRowsAsync(db, "equipment_type", type.Id, "INSERT");

        Assert.Equal(1, inserts);
    }

    [Fact]
    public async Task Audit_captures_before_and_after_on_update()
    {
        await using var db = fixture.CreateContext();

        var type = new EquipmentType
        {
            Code = "upd-" + Guid.NewGuid().ToString("N"),
            Name = "Before",
        };
        db.EquipmentTypes.Add(type);
        await db.SaveChangesAsync();

        type.Name = "After";
        await db.SaveChangesAsync();

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = new NpgsqlCommand(
            "SELECT old_data ->> 'name', new_data ->> 'name' FROM audit_log " +
            "WHERE table_name = 'equipment_type' AND record_pk = @pk AND operation = 'UPDATE'",
            conn);
        cmd.Parameters.AddWithValue("pk", type.Id.ToString());

        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("Before", reader.GetString(0));
        Assert.Equal("After", reader.GetString(1));
    }

    [Fact]
    public async Task Audit_log_rejects_update()
    {
        await using var db = fixture.CreateContext();

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.Database.ExecuteSqlRawAsync("UPDATE audit_log SET changed_by = 'tamper';"));

        Assert.Contains("append-only", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Audit_log_rejects_delete()
    {
        await using var db = fixture.CreateContext();

        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => db.Database.ExecuteSqlRawAsync("DELETE FROM audit_log;"));

        Assert.Contains("append-only", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------- Classification model ----------------

    [Fact]
    public async Task Equipment_type_can_belong_to_several_categories()
    {
        await using var db = fixture.CreateContext();

        var imaging = await db.Categories.SingleAsync(c => c.Code == "imaging");
        var diagnostic = await db.Categories.SingleAsync(c => c.Code == "diagnostic");

        // An ultrasound is genuinely both. A single category column could
        // not express this, which is why the join table exists.
        var ultrasound = new EquipmentType
        {
            Code = "us-" + Guid.NewGuid().ToString("N"),
            Name = "Ultrasound scanner",
        };
        db.EquipmentTypes.Add(ultrasound);
        await db.SaveChangesAsync();

        db.EquipmentTypeCategories.AddRange(
            new EquipmentTypeCategory { EquipmentTypeId = ultrasound.Id, CategoryId = imaging.Id, IsPrimary = true },
            new EquipmentTypeCategory { EquipmentTypeId = ultrasound.Id, CategoryId = diagnostic.Id, IsPrimary = false });
        await db.SaveChangesAsync();

        var links = await db.EquipmentTypeCategories
            .Where(x => x.EquipmentTypeId == ultrasound.Id)
            .ToListAsync();

        Assert.Equal(2, links.Count);
        Assert.Single(links, x => x.IsPrimary);
    }

    [Fact]
    public async Task An_equipment_type_cannot_have_two_primary_categories()
    {
        await using var db = fixture.CreateContext();

        var imaging = await db.Categories.SingleAsync(c => c.Code == "imaging");
        var diagnostic = await db.Categories.SingleAsync(c => c.Code == "diagnostic");

        var type = new EquipmentType
        {
            Code = "dual-" + Guid.NewGuid().ToString("N"),
            Name = "Two primaries",
        };
        db.EquipmentTypes.Add(type);
        await db.SaveChangesAsync();

        db.EquipmentTypeCategories.AddRange(
            new EquipmentTypeCategory { EquipmentTypeId = type.Id, CategoryId = imaging.Id, IsPrimary = true },
            new EquipmentTypeCategory { EquipmentTypeId = type.Id, CategoryId = diagnostic.Id, IsPrimary = true });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Category_codes_are_unique_per_tenant()
    {
        await using var db = fixture.CreateContext();

        db.Categories.Add(new Category { Code = "imaging", Name = "Duplicate imaging" });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static async Task<int> CountAuditRowsAsync(
        HospitalPmDbContext db,
        string table,
        int id,
        string operation)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE table_name = @t AND record_pk = @pk AND operation = @op",
            conn);
        cmd.Parameters.AddWithValue("t", table);
        cmd.Parameters.AddWithValue("pk", id.ToString());
        cmd.Parameters.AddWithValue("op", operation);

        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
