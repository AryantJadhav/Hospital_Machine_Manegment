using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SparePartSpecification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "compatible_models",
                table: "spare_part",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "manufacturer",
                table: "spare_part",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "manufacturer_part_number",
                table: "spare_part",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "specification",
                table: "spare_part",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "compatible_models",
                table: "spare_part");

            migrationBuilder.DropColumn(
                name: "manufacturer",
                table: "spare_part");

            migrationBuilder.DropColumn(
                name: "manufacturer_part_number",
                table: "spare_part");

            migrationBuilder.DropColumn(
                name: "specification",
                table: "spare_part");
        }
    }
}
