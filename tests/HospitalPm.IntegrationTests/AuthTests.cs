using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HospitalPm.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public sealed class AuthTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Both_roles_are_seeded_and_nothing_else()
    {
        await using var db = fixture.CreateContext();

        var names = await db.Roles.Select(r => r.Name).ToListAsync();

        // Exactly two, not at-least-two: the four-role seed is still in the
        // migration history, so this is what proves the collapse actually
        // removed BiomedicalHead, SeniorEngineer and Technician rather than
        // leaving them behind for someone to be assigned to.
        Assert.Equal(2, names.Count);
        foreach (var expected in Roles.All)
        {
            Assert.Contains(expected, names);
        }
    }

    [Fact]
    public async Task Role_normalized_names_are_seeded_correctly()
    {
        await using var db = fixture.CreateContext();

        // Identity looks roles up by normalized name. Seeding it wrong makes
        // every role check silently fail rather than raise an error.
        var roles = await db.Roles.ToListAsync();

        Assert.All(roles, r =>
            Assert.Equal(r.Name!.ToUpperInvariant(), r.NormalizedName));
    }

    [Fact]
    public async Task Identity_tables_all_carry_tenant_id()
    {
        await using var db = fixture.CreateContext();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        const string sql = @"
            SELECT t.table_name
            FROM information_schema.tables t
            WHERE t.table_schema = 'public'
              AND t.table_type = 'BASE TABLE'
              AND t.table_name LIKE 'app\_%'
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

    [Fact]
    public async Task Identity_tables_are_snake_case_not_aspnet_defaults()
    {
        await using var db = fixture.CreateContext();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables " +
            "WHERE table_schema = 'public' AND table_name LIKE 'AspNet%'",
            conn);

        var leaked = Convert.ToInt32(
            await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(0, leaked);
    }

    // ---------------- The security property that matters most ----------------

    [Fact]
    public async Task Audit_never_records_a_password_hash()
    {
        await using var db = fixture.CreateContext();

        var user = new ApplicationUser
        {
            UserName = "audit-probe-" + Guid.NewGuid().ToString("N")[..8],
            FullName = "Audit Probe",
            PasswordHash = "SUPER-SECRET-HASH-VALUE",
            SecurityStamp = "SUPER-SECRET-STAMP",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        user.FullName = "Audit Probe Renamed";
        await db.SaveChangesAsync();

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        // audit_log is append-only, so a secret written into it could never
        // be deleted. Redaction has to happen in the trigger, before the
        // insert — this asserts it did.
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log " +
            "WHERE table_name = 'app_user' " +
            "  AND (old_data::text LIKE '%SUPER-SECRET%' OR new_data::text LIKE '%SUPER-SECRET%')",
            conn);

        var leaks = Convert.ToInt32(
            await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(0, leaks);
    }

    [Fact]
    public async Task User_changes_are_still_audited_after_redaction()
    {
        await using var db = fixture.CreateContext();

        var user = new ApplicationUser
        {
            UserName = "audited-" + Guid.NewGuid().ToString("N")[..8],
            FullName = "Original Name",
            PasswordHash = "hash",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        user.FullName = "Changed Name";
        await db.SaveChangesAsync();

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        // Redaction must remove secrets without gutting the trail: the
        // non-sensitive before/after values still have to be there.
        await using var cmd = new NpgsqlCommand(
            "SELECT old_data ->> 'full_name', new_data ->> 'full_name' FROM audit_log " +
            "WHERE table_name = 'app_user' AND record_pk = @pk AND operation = 'UPDATE'",
            conn);
        cmd.Parameters.AddWithValue("pk", user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("Original Name", reader.GetString(0));
        Assert.Equal("Changed Name", reader.GetString(1));
    }

    [Fact]
    public async Task Refresh_token_hash_is_never_stored_in_the_audit_trail()
    {
        await using var db = fixture.CreateContext();

        var user = new ApplicationUser
        {
            UserName = "rt-" + Guid.NewGuid().ToString("N")[..8],
            FullName = "Refresh Probe",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = "DEADBEEF-TOKEN-HASH",
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE new_data::text LIKE '%DEADBEEF%'", conn);

        var leaks = Convert.ToInt32(
            await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(0, leaks);
    }

    [Fact]
    public async Task Role_assignment_is_audited()
    {
        await using var db = fixture.CreateContext();

        var user = new ApplicationUser
        {
            UserName = "roled-" + Guid.NewGuid().ToString("N")[..8],
            FullName = "Role Probe",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var technician = await db.Roles.SingleAsync(r => r.Name == Roles.Employee);

        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO app_user_role (tenant_id, user_id, role_id) VALUES (1, {0}, {1})",
            user.Id, technician.Id);

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        // "Who was given which role, and when" is the question a NABH
        // auditor actually asks.
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE table_name = 'app_user_role' " +
            "AND operation = 'INSERT' AND (new_data ->> 'user_id')::int = @uid",
            conn);
        cmd.Parameters.AddWithValue("uid", user.Id);

        var rows = Convert.ToInt32(
            await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task Refresh_token_hash_is_unique()
    {
        await using var db = fixture.CreateContext();

        var user = new ApplicationUser
        {
            UserName = "dup-" + Guid.NewGuid().ToString("N")[..8],
            FullName = "Duplicate Probe",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var hash = "UNIQUE-" + Guid.NewGuid().ToString("N");

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = hash,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
        });
        await db.SaveChangesAsync();

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = hash,
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
