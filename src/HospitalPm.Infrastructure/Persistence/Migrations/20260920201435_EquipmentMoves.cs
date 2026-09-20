using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EquipmentMoves : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "equipment_move",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    equipment_id = table.Column<int>(type: "integer", nullable: false),
                    from_location_id = table.Column<int>(type: "integer", nullable: true),
                    to_location_id = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    moved_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    moved_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equipment_move", x => x.id);
                    table.ForeignKey(
                        name: "FK_equipment_move_equipment_equipment_id",
                        column: x => x.equipment_id,
                        principalTable: "equipment",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_equipment_move_location_from_location_id",
                        column: x => x.from_location_id,
                        principalTable: "location",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_equipment_move_location_to_location_id",
                        column: x => x.to_location_id,
                        principalTable: "location",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_equipment_move_from_location_id",
                table: "equipment_move",
                column: "from_location_id");

            migrationBuilder.CreateIndex(
                name: "IX_equipment_move_to_location_id",
                table: "equipment_move",
                column: "to_location_id");

            migrationBuilder.CreateIndex(
                name: "ix_equipment_move_equipment",
                table: "equipment_move",
                columns: new[] { "equipment_id", "moved_at_utc" });

            // Audit is written by Postgres, never by application code.
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS trg_equipment_move_audit ON equipment_move;
                CREATE TRIGGER trg_equipment_move_audit
                    AFTER INSERT OR UPDATE OR DELETE ON equipment_move
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "equipment_move");
        }
    }
}
