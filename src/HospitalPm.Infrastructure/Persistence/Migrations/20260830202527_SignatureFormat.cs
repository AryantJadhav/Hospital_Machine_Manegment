using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SignatureFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "signature_png",
                table: "pm_completion",
                newName: "signature");

            migrationBuilder.AddColumn<string>(
                name: "signature_format",
                table: "pm_completion",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "signature_format",
                table: "pm_completion");

            migrationBuilder.RenameColumn(
                name: "signature",
                table: "pm_completion",
                newName: "signature_png");
        }
    }
}
