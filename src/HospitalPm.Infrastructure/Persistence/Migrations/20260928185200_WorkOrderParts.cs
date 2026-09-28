using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkOrderParts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "work_order_part",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    work_order_id = table.Column<int>(type: "integer", nullable: false),
                    spare_part_id = table.Column<int>(type: "integer", nullable: false),
                    quantity_used = table.Column<int>(type: "integer", nullable: false),
                    unit_cost_at_use = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    used_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    used_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_work_order_part", x => x.id);
                    table.ForeignKey(
                        name: "FK_work_order_part_spare_part_spare_part_id",
                        column: x => x.spare_part_id,
                        principalTable: "spare_part",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_work_order_part_work_order_work_order_id",
                        column: x => x.work_order_id,
                        principalTable: "work_order",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_work_order_part_order",
                table: "work_order_part",
                column: "work_order_id");

            migrationBuilder.CreateIndex(
                name: "ix_work_order_part_spare_part",
                table: "work_order_part",
                column: "spare_part_id");

            // Audit is written by Postgres, never by application code. Which
            // parts were drawn against which ticket is accountable evidence,
            // the same reasoning as equipment_move and spare_part.
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS trg_work_order_part_audit ON work_order_part;
                CREATE TRIGGER trg_work_order_part_audit
                    AFTER INSERT OR UPDATE OR DELETE ON work_order_part
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "work_order_part");
        }
    }
}
