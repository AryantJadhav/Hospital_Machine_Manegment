using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkOrderReplacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_order_replacement",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    work_order_id = table.Column<int>(type: "integer", nullable: false),
                    replacement_equipment_id = table.Column<int>(type: "integer", nullable: false),
                    from_location_id = table.Column<int>(type: "integer", nullable: false),
                    to_location_id = table.Column<int>(type: "integer", nullable: false),
                    status_before = table.Column<int>(type: "integer", nullable: false),
                    provided_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    provided_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    returned_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    returned_by_user_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_order_replacement", x => x.id);
                    table.ForeignKey(
                        name: "FK_work_order_replacement_equipment_replacement_equipment_id",
                        column: x => x.replacement_equipment_id,
                        principalTable: "equipment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_order_replacement_location_from_location_id",
                        column: x => x.from_location_id,
                        principalTable: "location",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_order_replacement_location_to_location_id",
                        column: x => x.to_location_id,
                        principalTable: "location",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_order_replacement_work_order_work_order_id",
                        column: x => x.work_order_id,
                        principalTable: "work_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_work_order_replacement_from_location_id",
                table: "work_order_replacement",
                column: "from_location_id");

            migrationBuilder.CreateIndex(
                name: "IX_work_order_replacement_to_location_id",
                table: "work_order_replacement",
                column: "to_location_id");

            migrationBuilder.CreateIndex(
                name: "ux_work_order_replacement_machine_active",
                table: "work_order_replacement",
                column: "replacement_equipment_id",
                unique: true,
                filter: "returned_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_work_order_replacement_order_active",
                table: "work_order_replacement",
                column: "work_order_id",
                unique: true,
                filter: "returned_at_utc IS NULL");

            // The rules the API checks, kept in the database too for anything that writes another way.
            migrationBuilder.Sql(@"
                ALTER TABLE work_order_replacement DROP CONSTRAINT IF EXISTS ck_work_order_replacement_status;
                ALTER TABLE work_order_replacement ADD CONSTRAINT ck_work_order_replacement_status
                    CHECK (status_before IN (10, 20, 30, 40, 50, 60));

                -- It is taken back exactly when it has a time and a person for it, and never before it was put there.
                ALTER TABLE work_order_replacement DROP CONSTRAINT IF EXISTS ck_work_order_replacement_returned;
                ALTER TABLE work_order_replacement ADD CONSTRAINT ck_work_order_replacement_returned
                    CHECK ((returned_at_utc IS NULL) = (returned_by_user_id IS NULL)
                       AND (returned_at_utc IS NULL OR returned_at_utc >= provided_at_utc));");

            // A loan that has been taken back is the record of how it went. It cannot be reopened or rewritten, and a row is
            // never deleted, by the application or by anything else that writes to the table.
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION fn_work_order_replacement_final() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'A replacement machine record cannot be deleted.'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    IF OLD.returned_at_utc IS NOT NULL THEN
                        RAISE EXCEPTION 'A replacement that has been taken back can no longer be changed.'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    -- While it is in place only the taking back may change: not who, what, where or when it was put.
                    IF NEW.work_order_id <> OLD.work_order_id
                       OR NEW.replacement_equipment_id <> OLD.replacement_equipment_id
                       OR NEW.from_location_id <> OLD.from_location_id
                       OR NEW.to_location_id <> OLD.to_location_id
                       OR NEW.status_before <> OLD.status_before
                       OR NEW.provided_at_utc <> OLD.provided_at_utc
                       OR NEW.provided_by_user_id <> OLD.provided_by_user_id THEN
                        RAISE EXCEPTION 'Only the taking back of a replacement machine can be recorded on it.'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $fn$;

                DROP TRIGGER IF EXISTS trg_work_order_replacement_final ON work_order_replacement;
                CREATE TRIGGER trg_work_order_replacement_final
                    BEFORE UPDATE OR DELETE ON work_order_replacement
                    FOR EACH ROW EXECUTE FUNCTION fn_work_order_replacement_final();");

            // Audit is written by Postgres, never by application code.
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS trg_work_order_replacement_audit ON work_order_replacement;
                CREATE TRIGGER trg_work_order_replacement_audit
                    AFTER INSERT OR UPDATE OR DELETE ON work_order_replacement
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_order_replacement");
        }
    }
}
