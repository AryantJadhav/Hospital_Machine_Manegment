using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Incidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "incident",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    equipment_id = table.Column<int>(type: "integer", nullable: false),
                    location_id = table.Column<int>(type: "integer", nullable: false),
                    incident_type = table.Column<int>(type: "integer", nullable: false),
                    occurred_on = table.Column<DateOnly>(type: "date", nullable: false),
                    occurred_at = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    place = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    description = table.Column<string>(type: "text", nullable: false),
                    involved_person = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    immediate_action = table.Column<string>(type: "text", nullable: true),
                    taken_out_of_use = table.Column<bool>(type: "boolean", nullable: false),
                    damage_level = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    findings = table.Column<string>(type: "text", nullable: true),
                    corrective_action = table.Column<string>(type: "text", nullable: true),
                    reported_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    reported_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    closed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incident", x => x.id);
                    table.ForeignKey(
                        name: "FK_incident_equipment_equipment_id",
                        column: x => x.equipment_id,
                        principalTable: "equipment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_incident_location_location_id",
                        column: x => x.location_id,
                        principalTable: "location",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_incident_location_id",
                table: "incident",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_equipment",
                table: "incident",
                column: "equipment_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_occurred_on",
                table: "incident",
                column: "occurred_on");

            migrationBuilder.CreateIndex(
                name: "ix_incident_reported_by",
                table: "incident",
                column: "reported_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_incident_status",
                table: "incident",
                column: "status");

            // The rules the API checks, kept in the database too for anything that writes another way.
            migrationBuilder.Sql(@"
                ALTER TABLE incident DROP CONSTRAINT IF EXISTS ck_incident_type;
                ALTER TABLE incident ADD CONSTRAINT ck_incident_type CHECK (incident_type IN (10, 20, 30, 40, 50, 90));

                ALTER TABLE incident DROP CONSTRAINT IF EXISTS ck_incident_damage;
                ALTER TABLE incident ADD CONSTRAINT ck_incident_damage CHECK (damage_level IN (10, 20, 30, 40));

                ALTER TABLE incident DROP CONSTRAINT IF EXISTS ck_incident_status;
                ALTER TABLE incident ADD CONSTRAINT ck_incident_status CHECK (status IN (10, 20, 30));

                ALTER TABLE incident DROP CONSTRAINT IF EXISTS ck_incident_description;
                ALTER TABLE incident ADD CONSTRAINT ck_incident_description CHECK (length(btrim(description)) > 0);

                -- It is Closed exactly when it has a time it was closed, and a closed one says what caused it.
                ALTER TABLE incident DROP CONSTRAINT IF EXISTS ck_incident_closed;
                ALTER TABLE incident ADD CONSTRAINT ck_incident_closed
                    CHECK ((status = 30) = (closed_at_utc IS NOT NULL)
                       AND (status <> 30 OR length(btrim(coalesce(findings, ''))) > 0));");

            // A closed incident is the record of what was found. It cannot be reopened or rewritten, by the
            // application or by anything else that writes to the table.
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION fn_incident_closed_is_final() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                    IF OLD.status = 30 THEN
                        RAISE EXCEPTION 'A closed incident can no longer be changed.'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $fn$;

                DROP TRIGGER IF EXISTS trg_incident_closed_is_final ON incident;
                CREATE TRIGGER trg_incident_closed_is_final
                    BEFORE UPDATE ON incident
                    FOR EACH ROW EXECUTE FUNCTION fn_incident_closed_is_final();");

            // Audit is written by Postgres, never by application code. What happened to a machine, who wrote it
            // up and who closed it, is a record the department can be asked to account for.
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS trg_incident_audit ON incident;
                CREATE TRIGGER trg_incident_audit
                    AFTER INSERT OR UPDATE OR DELETE ON incident
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "incident");
        }
    }
}
