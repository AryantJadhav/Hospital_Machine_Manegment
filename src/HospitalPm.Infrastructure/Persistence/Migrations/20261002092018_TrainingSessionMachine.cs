using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrainingSessionMachine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "equipment_id",
                table: "training_session",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_training_session_equipment",
                table: "training_session",
                column: "equipment_id");

            migrationBuilder.AddForeignKey(
                name: "FK_training_session_equipment_equipment_id",
                table: "training_session",
                column: "equipment_id",
                principalTable: "equipment",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_training_session_equipment_equipment_id",
                table: "training_session");

            migrationBuilder.DropIndex(
                name: "ix_training_session_equipment",
                table: "training_session");

            migrationBuilder.DropColumn(
                name: "equipment_id",
                table: "training_session");
        }
    }
}
