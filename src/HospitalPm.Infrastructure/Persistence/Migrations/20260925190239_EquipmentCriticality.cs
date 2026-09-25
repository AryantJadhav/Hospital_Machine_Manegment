using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EquipmentCriticality : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Nullable on purpose: every machine already on a register is
            // unclassified until someone decides, and guessing a level for two
            // thousand imported assets would be worse than leaving it blank.
            // Idempotent, like the other schema changes, so a re-run is harmless.
            migrationBuilder.Sql(@"
                ALTER TABLE equipment ADD COLUMN IF NOT EXISTS criticality integer NULL;

                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint WHERE conname = 'ck_equipment_criticality') THEN
                        ALTER TABLE equipment
                            ADD CONSTRAINT ck_equipment_criticality
                            CHECK (criticality IS NULL OR criticality IN (10, 20, 30));
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
