using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The migration from two roles to five, run the way a real installation meets it: a database that
/// already holds accounts. The shared test database is made new, so it can never prove this.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class FiveRolesMigrationTests(PostgresFixture fixture)
{
    private const string BeforeFiveRoles = "20260908073820_TwoRoles";

    private async Task<HospitalPmDbContext> FreshDatabaseAsync()
    {
        var name = $"mig_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(fixture.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{name}\"";
            await create.ExecuteNonQueryAsync();
        }

        var cs = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString;
        return new HospitalPmDbContext(new DbContextOptionsBuilder<HospitalPmDbContext>().UseNpgsql(cs).Options);
    }

    private static async Task<int> InsertUserAsync(HospitalPmDbContext db, string userName, string role)
    {
        // A command of its own: an INSERT ... RETURNING cannot be composed into a query.
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var insert = conn.CreateCommand();
        insert.CommandText =
            "INSERT INTO app_user (full_name, user_name, normalized_user_name, email_confirmed, phone_number_confirmed, "
            + "two_factor_enabled, lockout_enabled, access_failed_count) "
            + $"VALUES ('{userName}', '{userName}', '{userName.ToUpperInvariant()}', false, false, false, true, 0) RETURNING id";
        var id = (int)(await insert.ExecuteScalarAsync())!;

        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO app_user_role (user_id, role_id) SELECT {id}, id FROM app_role WHERE name = '{role}'");
        return id;
    }

    private static async Task<string> RoleOfAsync(HospitalPmDbContext db, int userId) =>
        await db.Database.SqlQueryRaw<string>(
            "SELECT r.name AS \"Value\" FROM app_user_role ur JOIN app_role r ON r.id = ur.role_id "
            + $"WHERE ur.user_id = {userId}").SingleAsync();

    [Fact]
    public async Task An_installation_with_accounts_keeps_every_one_and_its_first_administrator_becomes_the_developer()
    {
        await using var db = await FreshDatabaseAsync();
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync(BeforeFiveRoles);

        // The state a running installation was in: an administrator, a second administrator, an employee.
        var first = await InsertUserAsync(db, "first-admin", "Admin");
        var second = await InsertUserAsync(db, "second-admin", "Admin");
        var employee = await InsertUserAsync(db, "ward-tech", "Employee");

        await migrator.MigrateAsync();

        // Everyone keeps an account with a role, and nobody lost one: the first administrator is the
        // Developer (so someone can make the hospital's IT team), the other becomes head of department,
        // the employee an engineer.
        Assert.Equal("Developer", await RoleOfAsync(db, first));
        Assert.Equal("BmeHead", await RoleOfAsync(db, second));
        Assert.Equal("BmeEngineer", await RoleOfAsync(db, employee));

        var roles = await db.Database.SqlQueryRaw<string>("SELECT name AS \"Value\" FROM app_role ORDER BY name").ToListAsync();
        Assert.Equal(["BmeEngineer", "BmeHead", "DepartmentUser", "Developer", "ItAdmin"], roles);

        // The lookup key Identity uses is right, or every role check would fail silently.
        var bad = await db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM app_role WHERE normalized_name <> upper(name)").SingleAsync();
        Assert.Equal(0, bad);

        // And the tables the later steps added are there.
        foreach (var table in new[] { "user_permission", "user_location" })
        {
            var exists = await db.Database.SqlQueryRaw<int>(
                $"SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = '{table}'").SingleAsync();
            Assert.Equal(1, exists);
        }
    }

    [Fact]
    public async Task An_installation_with_a_single_administrator_gets_a_developer_and_nobody_is_left_without_a_role()
    {
        await using var db = await FreshDatabaseAsync();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeFiveRoles);

        var only = await InsertUserAsync(db, "only-admin", "Admin");
        await migrator.MigrateAsync();

        Assert.Equal("Developer", await RoleOfAsync(db, only));

        var withoutRole = await db.Database.SqlQueryRaw<int>(
            "SELECT count(*)::int AS \"Value\" FROM app_user u WHERE NOT EXISTS (SELECT 1 FROM app_user_role ur WHERE ur.user_id = u.id)")
            .SingleAsync();
        Assert.Equal(0, withoutRole);
    }

    [Fact]
    public async Task An_empty_installation_gets_the_five_roles_and_no_developer_is_invented()
    {
        await using var db = await FreshDatabaseAsync();
        await db.GetService<IMigrator>().MigrateAsync();

        var users = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM app_user").SingleAsync();
        Assert.Equal(0, users);

        var roles = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM app_role").SingleAsync();
        Assert.Equal(5, roles);
    }

    [Fact]
    public async Task Migrating_again_changes_nothing()
    {
        await using var db = await FreshDatabaseAsync();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(BeforeFiveRoles);
        var only = await InsertUserAsync(db, "twice-admin", "Admin");

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        Assert.Equal("Developer", await RoleOfAsync(db, only));
        Assert.Equal(5, await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM app_role").SingleAsync());
    }
}
