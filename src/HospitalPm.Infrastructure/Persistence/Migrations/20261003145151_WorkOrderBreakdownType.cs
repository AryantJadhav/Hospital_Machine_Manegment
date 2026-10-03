using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkOrderBreakdownType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "breakdown_type",
                table: "work_order",
                type: "integer",
                nullable: true);

            // Blank until someone knows; otherwise one of the five kinds. Kept in the database too, for anything
            // that writes another way. Requests raised before this existed stay blank.
            migrationBuilder.Sql(@"
                ALTER TABLE work_order DROP CONSTRAINT IF EXISTS ck_work_order_breakdown_type;
                ALTER TABLE work_order ADD CONSTRAINT ck_work_order_breakdown_type
                    CHECK (breakdown_type IS NULL OR breakdown_type IN (10, 20, 30, 40, 50));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "breakdown_type",
                table: "work_order");
        }
    }
}
