using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SpareParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "spare_part",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    part_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    equipment_type_id = table.Column<int>(type: "integer", nullable: true),
                    unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "pcs"),
                    quantity_on_hand = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    reorder_level = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    unit_cost = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    supplier = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    storage_location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    updated_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_spare_part", x => x.id);
                    table.ForeignKey(
                        name: "FK_spare_part_equipment_type_equipment_type_id",
                        column: x => x.equipment_type_id,
                        principalTable: "equipment_type",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_spare_part_active_quantity",
                table: "spare_part",
                columns: new[] { "is_active", "quantity_on_hand" });

            migrationBuilder.CreateIndex(
                name: "ix_spare_part_equipment_type",
                table: "spare_part",
                column: "equipment_type_id");

            migrationBuilder.CreateIndex(
                name: "ux_spare_part_tenant_number",
                table: "spare_part",
                columns: new[] { "tenant_id", "part_number" },
                unique: true);

            // Audit is written by Postgres, never by application code. A stock
            // count is a record the department can be asked to account for, the
            // same reasoning as equipment_move.
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS trg_spare_part_audit ON spare_part;
                CREATE TRIGGER trg_spare_part_audit
                    AFTER INSERT OR UPDATE OR DELETE ON spare_part
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "spare_part");
        }
    }
}
