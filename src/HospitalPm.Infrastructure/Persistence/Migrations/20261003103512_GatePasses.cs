using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GatePasses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence<int>(
                name: "gate_pass_number_seq",
                startValue: 1001L);

            migrationBuilder.CreateTable(
                name: "gate_pass",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    number = table.Column<int>(type: "integer", nullable: false, defaultValueSql: "nextval('gate_pass_number_seq')"),
                    pass_date = table.Column<DateOnly>(type: "date", nullable: false),
                    vendor_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    contact_person = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    contact_phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    purpose = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    work_order_id = table.Column<int>(type: "integer", nullable: true),
                    expected_return_date = table.Column<DateOnly>(type: "date", nullable: true),
                    returned_on = table.Column<DateOnly>(type: "date", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    authorised_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    outcome_notes = table.Column<string>(type: "text", nullable: true),
                    created_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gate_pass", x => x.id);
                    table.ForeignKey(
                        name: "FK_gate_pass_work_order_work_order_id",
                        column: x => x.work_order_id,
                        principalTable: "work_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "gate_pass_item",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    gate_pass_id = table.Column<int>(type: "integer", nullable: false),
                    equipment_id = table.Column<int>(type: "integer", nullable: true),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    asset_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gate_pass_item", x => x.id);
                    table.ForeignKey(
                        name: "FK_gate_pass_item_equipment_equipment_id",
                        column: x => x.equipment_id,
                        principalTable: "equipment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gate_pass_item_gate_pass_gate_pass_id",
                        column: x => x.gate_pass_id,
                        principalTable: "gate_pass",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_gate_pass_date",
                table: "gate_pass",
                column: "pass_date");

            migrationBuilder.CreateIndex(
                name: "ix_gate_pass_status",
                table: "gate_pass",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "ix_gate_pass_work_order",
                table: "gate_pass",
                column: "work_order_id");

            migrationBuilder.CreateIndex(
                name: "ux_gate_pass_number",
                table: "gate_pass",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_gate_pass_item_equipment",
                table: "gate_pass_item",
                column: "equipment_id");

            migrationBuilder.CreateIndex(
                name: "ix_gate_pass_item_pass",
                table: "gate_pass_item",
                column: "gate_pass_id");

            // The rules the API checks, kept in the database too for anything that writes another way.
            migrationBuilder.Sql(@"
                ALTER TABLE gate_pass DROP CONSTRAINT IF EXISTS ck_gate_pass_status;
                ALTER TABLE gate_pass ADD CONSTRAINT ck_gate_pass_status CHECK (status IN (10, 20, 30));

                ALTER TABLE gate_pass DROP CONSTRAINT IF EXISTS ck_gate_pass_vendor;
                ALTER TABLE gate_pass ADD CONSTRAINT ck_gate_pass_vendor
                    CHECK (length(btrim(vendor_name)) > 0 AND length(btrim(purpose)) > 0);

                -- It is Returned exactly when it has a day it came back, so the two cannot disagree.
                ALTER TABLE gate_pass DROP CONSTRAINT IF EXISTS ck_gate_pass_returned;
                ALTER TABLE gate_pass ADD CONSTRAINT ck_gate_pass_returned
                    CHECK ((status = 20) = (returned_on IS NOT NULL));

                ALTER TABLE gate_pass DROP CONSTRAINT IF EXISTS ck_gate_pass_dates;
                ALTER TABLE gate_pass ADD CONSTRAINT ck_gate_pass_dates
                    CHECK ((expected_return_date IS NULL OR expected_return_date >= pass_date)
                       AND (returned_on IS NULL OR returned_on >= pass_date));

                ALTER TABLE gate_pass_item DROP CONSTRAINT IF EXISTS ck_gate_pass_item_text;
                ALTER TABLE gate_pass_item ADD CONSTRAINT ck_gate_pass_item_text
                    CHECK (length(btrim(description)) > 0);

                ALTER TABLE gate_pass_item DROP CONSTRAINT IF EXISTS ck_gate_pass_item_quantity;
                ALTER TABLE gate_pass_item ADD CONSTRAINT ck_gate_pass_item_quantity
                    CHECK (quantity BETWEEN 1 AND 100000);");

            // What went out is what went out. Once a pass is returned or cancelled its lines are the record
            // of what left the hospital, and they cannot be added to, changed or taken off.
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION fn_gate_pass_item_lock() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                DECLARE
                    v_status int;
                BEGIN
                    SELECT status INTO v_status FROM gate_pass
                    WHERE id = CASE WHEN TG_OP = 'DELETE' THEN OLD.gate_pass_id ELSE NEW.gate_pass_id END;

                    IF v_status IS NOT NULL AND v_status <> 10 THEN
                        RAISE EXCEPTION 'The items on a gate pass that has been returned or cancelled cannot be changed.'
                            USING ERRCODE = 'check_violation';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;
                    RETURN NEW;
                END;
                $fn$;

                DROP TRIGGER IF EXISTS trg_gate_pass_item_lock ON gate_pass_item;
                CREATE TRIGGER trg_gate_pass_item_lock
                    BEFORE INSERT OR UPDATE OR DELETE ON gate_pass_item
                    FOR EACH ROW EXECUTE FUNCTION fn_gate_pass_item_lock();");

            // Audit is written by Postgres, never by application code. Which machines left the hospital,
            // with whom, and when they came back is a record the department can be asked to account for.
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS trg_gate_pass_audit ON gate_pass;
                CREATE TRIGGER trg_gate_pass_audit
                    AFTER INSERT OR UPDATE OR DELETE ON gate_pass
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();

                DROP TRIGGER IF EXISTS trg_gate_pass_item_audit ON gate_pass_item;
                CREATE TRIGGER trg_gate_pass_item_audit
                    AFTER INSERT OR UPDATE OR DELETE ON gate_pass_item
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "gate_pass_item");

            migrationBuilder.DropTable(
                name: "gate_pass");

            migrationBuilder.DropSequence(
                name: "gate_pass_number_seq");
        }
    }
}
