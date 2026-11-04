using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReturnablePartsAndNonReturnableGatePass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_returnable",
                table: "spare_part",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "spare_part_id",
                table: "gate_pass_item",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_returnable",
                table: "gate_pass",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // 40 is a non-returnable pass: issued, and never expected back. A returnable pass is never 40, and a
            // non-returnable one has no day it is due back or came back, so the two kinds cannot be mixed up.
            migrationBuilder.Sql(@"
                ALTER TABLE gate_pass DROP CONSTRAINT IF EXISTS ck_gate_pass_status;
                ALTER TABLE gate_pass ADD CONSTRAINT ck_gate_pass_status CHECK (status IN (10, 20, 30, 40));

                ALTER TABLE gate_pass DROP CONSTRAINT IF EXISTS ck_gate_pass_kind;
                ALTER TABLE gate_pass ADD CONSTRAINT ck_gate_pass_kind
                    CHECK ((status <> 40 OR NOT is_returnable)
                       AND (is_returnable OR (expected_return_date IS NULL AND returned_on IS NULL AND status <> 20)));");

            migrationBuilder.CreateIndex(
                name: "ix_gate_pass_item_spare_part",
                table: "gate_pass_item",
                column: "spare_part_id");

            migrationBuilder.AddForeignKey(
                name: "FK_gate_pass_item_spare_part_spare_part_id",
                table: "gate_pass_item",
                column: "spare_part_id",
                principalTable: "spare_part",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_gate_pass_item_spare_part_spare_part_id",
                table: "gate_pass_item");

            migrationBuilder.DropIndex(
                name: "ix_gate_pass_item_spare_part",
                table: "gate_pass_item");

            migrationBuilder.DropColumn(
                name: "is_returnable",
                table: "spare_part");

            migrationBuilder.DropColumn(
                name: "spare_part_id",
                table: "gate_pass_item");

            migrationBuilder.DropColumn(
                name: "is_returnable",
                table: "gate_pass");
        }
    }
}
