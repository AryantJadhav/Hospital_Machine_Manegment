using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TrainingSessionTrainerType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "trainer_type",
                table: "training_session",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.Sql(@"
                ALTER TABLE training_session DROP CONSTRAINT IF EXISTS ck_training_session_trainer_type;
                ALTER TABLE training_session
                    ADD CONSTRAINT ck_training_session_trainer_type
                    CHECK (trainer_type IS NULL OR trainer_type IN ('Vendor', 'InHouse'));
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "trainer_type",
                table: "training_session");
        }
    }
}
