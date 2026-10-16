using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExpectedLifeAndResponseTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "expected_life_years",
                table: "equipment",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "response_target",
                columns: table => new
                {
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    minutes = table.Column<int>(type: "integer", nullable: false),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_response_target", x => new { x.tenant_id, x.priority });
                });

            // Whole years from 1 to 60, or blank. Kept in the database as well as the form, for anything that writes another way.
            migrationBuilder.Sql(@"
                ALTER TABLE equipment DROP CONSTRAINT IF EXISTS ck_equipment_expected_life_years;
                ALTER TABLE equipment ADD CONSTRAINT ck_equipment_expected_life_years
                    CHECK (expected_life_years IS NULL OR (expected_life_years BETWEEN 1 AND 60));");

            // One target per priority (10 Low, 20 Medium, 30 High, 40 Critical), from one minute to thirty days.
            migrationBuilder.Sql(@"
                ALTER TABLE response_target DROP CONSTRAINT IF EXISTS ck_response_target_values;
                ALTER TABLE response_target ADD CONSTRAINT ck_response_target_values
                    CHECK (priority IN (10, 20, 30, 40) AND minutes BETWEEN 1 AND 43200);");

            // What a hospital starts with, before the head changes it: Critical 15 minutes, High 1 hour, Medium 4 hours,
            // Low 1 day. Idempotent: running it again never overwrites a target someone has set.
            migrationBuilder.Sql(@"
                INSERT INTO response_target (tenant_id, priority, minutes) VALUES
                    (1, 40, 15), (1, 30, 60), (1, 20, 240), (1, 10, 1440)
                ON CONFLICT (tenant_id, priority) DO NOTHING;");

            // Audit is written by Postgres, never by application code: who changed how fast the team must respond.
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS trg_response_target_audit ON response_target;
                CREATE TRIGGER trg_response_target_audit
                    AFTER INSERT OR UPDATE OR DELETE ON response_target
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "response_target");

            migrationBuilder.DropColumn(
                name: "expected_life_years",
                table: "equipment");
        }
    }
}
