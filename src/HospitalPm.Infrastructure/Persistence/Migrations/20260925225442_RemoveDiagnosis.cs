using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDiagnosis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The daily check (Diagnose) is gone from the product. Its table goes with it, but only
            // when it holds nothing: a check that was recorded is a record, and a migration should
            // not destroy one silently. In a database that has any, the table is left where it is,
            // untouched and unused, for whoever needs to read it. Idempotent.
            migrationBuilder.Sql(@"
                DO $$
                BEGIN
                    IF to_regclass('public.diagnosis') IS NOT NULL THEN
                        IF NOT EXISTS (SELECT 1 FROM diagnosis) THEN
                            DROP TABLE diagnosis;
                        ELSE
                            RAISE NOTICE 'Table diagnosis holds records, so it has been kept.';
                        END IF;
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
