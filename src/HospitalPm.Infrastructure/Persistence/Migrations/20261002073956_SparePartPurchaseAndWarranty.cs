using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SparePartPurchaseAndWarranty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "purchase_date",
                table: "spare_part",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "warranty_months",
                table: "spare_part",
                type: "integer",
                nullable: true);

            // A warranty is a whole number of months, at least one and not more than fifty years, and
            // needs a purchase date to run from. The API checks the same; this is for anything that
            // writes to the table another way.
            migrationBuilder.Sql(@"
                ALTER TABLE spare_part DROP CONSTRAINT IF EXISTS ck_spare_part_warranty;
                ALTER TABLE spare_part
                    ADD CONSTRAINT ck_spare_part_warranty CHECK (
                        warranty_months IS NULL
                        OR (warranty_months BETWEEN 1 AND 600 AND purchase_date IS NOT NULL));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "purchase_date",
                table: "spare_part");

            migrationBuilder.DropColumn(
                name: "warranty_months",
                table: "spare_part");
        }
    }
}
