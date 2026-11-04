using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LossApprovalChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_equipment_loss_open",
                table: "equipment_loss");

            migrationBuilder.AddColumn<DateTime>(
                name: "approved_at_utc",
                table: "equipment_loss",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "approved_by_user_id",
                table: "equipment_loss",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "decision_notes",
                table: "equipment_loss",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_equipment_loss_open",
                table: "equipment_loss",
                column: "equipment_id",
                unique: true,
                filter: "status IN (10, 20, 25)");

            // A loss that was already written off before approval existed counts as approved by the person who closed it, so the
            // new rule below holds for it too. The trigger that keeps a closed loss final is lifted for this one backfill only.
            migrationBuilder.Sql(@"
                ALTER TABLE equipment_loss DISABLE TRIGGER trg_equipment_loss_closed_is_final;
                UPDATE equipment_loss
                   SET approved_by_user_id = COALESCE(closed_by_user_id, reported_by_user_id),
                       approved_at_utc = COALESCE(closed_at_utc, reported_at_utc)
                 WHERE status = 40 AND approved_at_utc IS NULL;
                ALTER TABLE equipment_loss ENABLE TRIGGER trg_equipment_loss_closed_is_final;");

            // 25 is a loss waiting for the head's decision. It is there only after an engineer approved it, and a write-off
            // (40) is only ever made from there, so both carry the approval.
            migrationBuilder.Sql(@"
                ALTER TABLE equipment_loss DROP CONSTRAINT IF EXISTS ck_equipment_loss_status;
                ALTER TABLE equipment_loss ADD CONSTRAINT ck_equipment_loss_status CHECK (status IN (10, 20, 25, 30, 40, 50));

                ALTER TABLE equipment_loss DROP CONSTRAINT IF EXISTS ck_equipment_loss_approval;
                ALTER TABLE equipment_loss ADD CONSTRAINT ck_equipment_loss_approval
                    CHECK ((status NOT IN (25, 40) OR (approved_at_utc IS NOT NULL AND approved_by_user_id IS NOT NULL)));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_equipment_loss_open",
                table: "equipment_loss");

            migrationBuilder.DropColumn(
                name: "approved_at_utc",
                table: "equipment_loss");

            migrationBuilder.DropColumn(
                name: "approved_by_user_id",
                table: "equipment_loss");

            migrationBuilder.DropColumn(
                name: "decision_notes",
                table: "equipment_loss");

            migrationBuilder.CreateIndex(
                name: "ux_equipment_loss_open",
                table: "equipment_loss",
                column: "equipment_id",
                unique: true,
                filter: "status IN (10, 20)");
        }
    }
}
