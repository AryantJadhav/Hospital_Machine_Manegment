using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PastInsurancePolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "equipment_insurance_history",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    equipment_id = table.Column<int>(type: "integer", nullable: false),
                    provider = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    policy_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    expiry_date = table.Column<DateOnly>(type: "date", nullable: false),
                    cost = table.Column<decimal>(type: "numeric(14,2)", precision: 14, scale: 2, nullable: true),
                    renewed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    renewed_by_user_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equipment_insurance_history", x => x.id);
                    table.ForeignKey(
                        name: "FK_equipment_insurance_history_equipment_equipment_id",
                        column: x => x.equipment_id,
                        principalTable: "equipment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_equipment_insurance_history_equipment",
                table: "equipment_insurance_history",
                column: "equipment_id");

            migrationBuilder.Sql(@"
                ALTER TABLE equipment_insurance_history
                    ADD CONSTRAINT ck_equipment_insurance_history_cost CHECK (cost IS NULL OR cost >= 0);

                -- What was in force, and what it cost, is a record: added when a policy is renewed
                -- and never edited afterwards.
                CREATE OR REPLACE FUNCTION fn_equipment_insurance_history_no_update() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                    RAISE EXCEPTION 'Insurance history row % is a record and cannot be changed', OLD.id
                        USING ERRCODE = 'check_violation';
                END;
                $fn$;

                DROP TRIGGER IF EXISTS trg_equipment_insurance_history_no_update ON equipment_insurance_history;
                CREATE TRIGGER trg_equipment_insurance_history_no_update
                    BEFORE UPDATE ON equipment_insurance_history
                    FOR EACH ROW EXECUTE FUNCTION fn_equipment_insurance_history_no_update();

                -- Audit is written by Postgres, never by application code.
                DROP TRIGGER IF EXISTS trg_equipment_insurance_history_audit ON equipment_insurance_history;
                CREATE TRIGGER trg_equipment_insurance_history_audit
                    AFTER INSERT OR UPDATE OR DELETE ON equipment_insurance_history
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "equipment_insurance_history");
        }
    }
}
