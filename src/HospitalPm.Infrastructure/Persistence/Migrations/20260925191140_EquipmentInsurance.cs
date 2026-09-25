using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EquipmentInsurance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Every machine already on a register starts as not insured, which is
            // the honest answer until someone records a policy. Idempotent, like
            // the other schema changes, so a re-run is harmless.
            migrationBuilder.Sql(@"
                ALTER TABLE equipment ADD COLUMN IF NOT EXISTS is_insured boolean NOT NULL DEFAULT false;
                ALTER TABLE equipment ADD COLUMN IF NOT EXISTS insurance_provider varchar(200) NULL;
                ALTER TABLE equipment ADD COLUMN IF NOT EXISTS insurance_policy_number varchar(100) NULL;
                ALTER TABLE equipment ADD COLUMN IF NOT EXISTS insurance_expiry_date date NULL;

                -- Not insured means no details, and insured means an insurer and an
                -- expiry date. A machine cannot say 'no' while still carrying a policy
                -- that looks current, or say 'yes' with nothing to renew.
                DO $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM pg_constraint WHERE conname = 'ck_equipment_insurance') THEN
                        ALTER TABLE equipment
                            ADD CONSTRAINT ck_equipment_insurance
                            CHECK (
                                (is_insured
                                    AND insurance_provider IS NOT NULL
                                    AND insurance_expiry_date IS NOT NULL)
                                OR
                                (NOT is_insured
                                    AND insurance_provider IS NULL
                                    AND insurance_policy_number IS NULL
                                    AND insurance_expiry_date IS NULL));
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
