using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EquipmentMaintenanceContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No contract is the absence of a type, so there is no "none" value to keep
            // in step with the rest. Idempotent, like the other schema changes, so a
            // re-run is harmless.
            migrationBuilder.Sql(@"
                ALTER TABLE equipment ADD COLUMN IF NOT EXISTS maintenance_contract_type integer NULL;
                ALTER TABLE equipment ADD COLUMN IF NOT EXISTS maintenance_vendor varchar(200) NULL;
                ALTER TABLE equipment ADD COLUMN IF NOT EXISTS maintenance_contract_number varchar(100) NULL;
                ALTER TABLE equipment ADD COLUMN IF NOT EXISTS maintenance_start_date date NULL;
                ALTER TABLE equipment ADD COLUMN IF NOT EXISTS maintenance_end_date date NULL;
                ALTER TABLE equipment ADD COLUMN IF NOT EXISTS maintenance_cost numeric(14,2) NULL;

                -- A contract is AMC or CMC with a vendor and a period that does not end
                -- before it starts; no contract means none of the details. A machine
                -- cannot be left holding a contract it says it does not have.
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint WHERE conname = 'ck_equipment_maintenance_contract') THEN
                        ALTER TABLE equipment
                            ADD CONSTRAINT ck_equipment_maintenance_contract
                            CHECK (
                                (maintenance_contract_type IN (10, 20)
                                    AND maintenance_vendor IS NOT NULL
                                    AND maintenance_start_date IS NOT NULL
                                    AND maintenance_end_date IS NOT NULL
                                    AND maintenance_end_date >= maintenance_start_date
                                    AND (maintenance_cost IS NULL OR maintenance_cost >= 0))
                                OR
                                (maintenance_contract_type IS NULL
                                    AND maintenance_vendor IS NULL
                                    AND maintenance_contract_number IS NULL
                                    AND maintenance_start_date IS NULL
                                    AND maintenance_end_date IS NULL
                                    AND maintenance_cost IS NULL));
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
