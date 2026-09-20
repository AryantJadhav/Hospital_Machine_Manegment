using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Diagnoses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "kind",
                table: "checklist_template",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.CreateTable(
                name: "diagnosis",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    equipment_id = table.Column<int>(type: "integer", nullable: false),
                    checklist_template_version_id = table.Column<int>(type: "integer", nullable: false),
                    answers = table.Column<string>(type: "jsonb", nullable: false),
                    outcome = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    location_id = table.Column<int>(type: "integer", nullable: false),
                    performed_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    performed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    client_submission_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_diagnosis", x => x.id);
                    table.ForeignKey(
                        name: "FK_diagnosis_checklist_template_version_checklist_template_ver~",
                        column: x => x.checklist_template_version_id,
                        principalTable: "checklist_template_version",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_diagnosis_equipment_equipment_id",
                        column: x => x.equipment_id,
                        principalTable: "equipment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_diagnosis_location_location_id",
                        column: x => x.location_id,
                        principalTable: "location",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_diagnosis_checklist_template_version_id",
                table: "diagnosis",
                column: "checklist_template_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_diagnosis_location_id",
                table: "diagnosis",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ix_diagnosis_equipment",
                table: "diagnosis",
                columns: new[] { "equipment_id", "performed_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_diagnosis_performed_at",
                table: "diagnosis",
                column: "performed_at_utc");

            migrationBuilder.CreateIndex(
                name: "ux_diagnosis_client_submission",
                table: "diagnosis",
                column: "client_submission_id",
                unique: true,
                filter: "client_submission_id IS NOT NULL");

            // A diagnosis is a record of what was found, on the day, by whom. It cannot
            // be revised afterwards; a mistake is put right by checking again.
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION fn_diagnosis_immutable() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                    RAISE EXCEPTION
                        'Diagnosis % is a record and cannot be %; check the machine again instead',
                        OLD.id,
                        CASE TG_OP WHEN 'UPDATE' THEN 'changed' ELSE 'deleted' END
                        USING ERRCODE = 'check_violation';
                END;
                $fn$;

                DROP TRIGGER IF EXISTS trg_diagnosis_immutable ON diagnosis;
                CREATE TRIGGER trg_diagnosis_immutable
                    BEFORE UPDATE OR DELETE ON diagnosis
                    FOR EACH ROW EXECUTE FUNCTION fn_diagnosis_immutable();

                DROP TRIGGER IF EXISTS trg_diagnosis_audit ON diagnosis;
                CREATE TRIGGER trg_diagnosis_audit
                    AFTER INSERT OR UPDATE OR DELETE ON diagnosis
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "diagnosis");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "checklist_template");
        }
    }
}
