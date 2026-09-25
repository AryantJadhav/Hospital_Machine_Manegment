using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EquipmentCosts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Both nullable: a cost nobody recorded is unknown, not zero. Idempotent,
            // like the other schema changes, so a re-run is harmless.
            migrationBuilder.Sql(@"
                ALTER TABLE equipment ADD COLUMN IF NOT EXISTS purchase_cost numeric(14,2) NULL;
                ALTER TABLE equipment ADD COLUMN IF NOT EXISTS insurance_cost numeric(14,2) NULL;

                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint WHERE conname = 'ck_equipment_costs') THEN
                        ALTER TABLE equipment
                            ADD CONSTRAINT ck_equipment_costs
                            CHECK (
                                (purchase_cost IS NULL OR purchase_cost >= 0)
                                AND (insurance_cost IS NULL OR insurance_cost >= 0)
                                -- No insurance, no cost of it: the same rule as the other details.
                                AND (is_insured OR insurance_cost IS NULL));
                    END IF;
                END
                $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
