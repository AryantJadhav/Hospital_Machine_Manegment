using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EquipmentLoss : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "equipment_loss",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    equipment_id = table.Column<int>(type: "integer", nullable: false),
                    location_id = table.Column<int>(type: "integer", nullable: false),
                    loss_kind = table.Column<int>(type: "integer", nullable: false),
                    noticed_on = table.Column<DateOnly>(type: "date", nullable: false),
                    last_seen_on = table.Column<DateOnly>(type: "date", nullable: true),
                    last_seen_place = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    description = table.Column<string>(type: "text", nullable: false),
                    last_handled_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    police_report_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    police_reported_on = table.Column<DateOnly>(type: "date", nullable: true),
                    estimated_value = table.Column<decimal>(type: "numeric(14,2)", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    findings = table.Column<string>(type: "text", nullable: true),
                    corrective_action = table.Column<string>(type: "text", nullable: true),
                    recovered_on = table.Column<DateOnly>(type: "date", nullable: true),
                    equipment_status_before = table.Column<int>(type: "integer", nullable: true),
                    reported_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    reported_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equipment_loss", x => x.id);
                    table.ForeignKey(
                        name: "FK_equipment_loss_equipment_equipment_id",
                        column: x => x.equipment_id,
                        principalTable: "equipment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_equipment_loss_location_location_id",
                        column: x => x.location_id,
                        principalTable: "location",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_equipment_loss_location_id",
                table: "equipment_loss",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ix_equipment_loss_noticed_on",
                table: "equipment_loss",
                column: "noticed_on");

            migrationBuilder.CreateIndex(
                name: "ix_equipment_loss_reported_by",
                table: "equipment_loss",
                column: "reported_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_equipment_loss_status",
                table: "equipment_loss",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ux_equipment_loss_open",
                table: "equipment_loss",
                column: "equipment_id",
                unique: true,
                filter: "status IN (10, 20)");

            // The rules the API checks, kept in the database too for anything that writes another way.
            migrationBuilder.Sql(@"
                ALTER TABLE equipment_loss DROP CONSTRAINT IF EXISTS ck_equipment_loss_kind;
                ALTER TABLE equipment_loss ADD CONSTRAINT ck_equipment_loss_kind CHECK (loss_kind IN (10, 20, 30));

                ALTER TABLE equipment_loss DROP CONSTRAINT IF EXISTS ck_equipment_loss_status;
                ALTER TABLE equipment_loss ADD CONSTRAINT ck_equipment_loss_status CHECK (status IN (10, 20, 30, 40, 50));

                ALTER TABLE equipment_loss DROP CONSTRAINT IF EXISTS ck_equipment_loss_description;
                ALTER TABLE equipment_loss ADD CONSTRAINT ck_equipment_loss_description CHECK (length(btrim(description)) > 0);

                ALTER TABLE equipment_loss DROP CONSTRAINT IF EXISTS ck_equipment_loss_value;
                ALTER TABLE equipment_loss ADD CONSTRAINT ck_equipment_loss_value CHECK (estimated_value IS NULL OR estimated_value >= 0);

                -- It was last seen on or before the day it was noticed to be gone.
                ALTER TABLE equipment_loss DROP CONSTRAINT IF EXISTS ck_equipment_loss_dates;
                ALTER TABLE equipment_loss ADD CONSTRAINT ck_equipment_loss_dates
                    CHECK (last_seen_on IS NULL OR last_seen_on <= noticed_on);

                -- It is over (recovered, written off or cancelled) exactly when it has a time it was closed. It is
                -- Recovered exactly when it has a day it was found, and a loss that is written off says how it went.
                ALTER TABLE equipment_loss DROP CONSTRAINT IF EXISTS ck_equipment_loss_closed;
                ALTER TABLE equipment_loss ADD CONSTRAINT ck_equipment_loss_closed
                    CHECK ((status IN (30, 40, 50)) = (closed_at_utc IS NOT NULL)
                       AND ((status = 30) = (recovered_on IS NOT NULL))
                       AND (status <> 40 OR length(btrim(coalesce(findings, ''))) > 0));");

            // A loss that is over is the record of how it ended. It cannot be reopened or rewritten, by the application
            // or by anything else that writes to the table.
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION fn_equipment_loss_closed_is_final() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                    IF OLD.status IN (30, 40, 50) THEN
                        RAISE EXCEPTION 'A loss that has been closed can no longer be changed.'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $fn$;

                DROP TRIGGER IF EXISTS trg_equipment_loss_closed_is_final ON equipment_loss;
                CREATE TRIGGER trg_equipment_loss_closed_is_final
                    BEFORE UPDATE ON equipment_loss
                    FOR EACH ROW EXECUTE FUNCTION fn_equipment_loss_closed_is_final();");

            // Audit is written by Postgres, never by application code. Who wrote a loss up, and who closed it, is a record
            // the department can be asked to account for.
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS trg_equipment_loss_audit ON equipment_loss;
                CREATE TRIGGER trg_equipment_loss_audit
                    AFTER INSERT OR UPDATE OR DELETE ON equipment_loss
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "equipment_loss");
        }
    }
}
