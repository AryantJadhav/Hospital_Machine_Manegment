using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IssuedLicences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "issued_licence",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    tenant_id = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    licence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    hospital_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    issued_on = table.Column<DateOnly>(type: "date", nullable: false),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    duration_days = table.Column<int>(type: "integer", nullable: true),
                    max_equipment = table.Column<int>(type: "integer", nullable: true),
                    modules = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false, defaultValue: ""),
                    notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    licence_text = table.Column<string>(type: "text", nullable: false),
                    issued_by_user_id = table.Column<int>(type: "integer", nullable: false),
                    issued_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    lock_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L),
                    is_locked = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    lock_changed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_issued_licence", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_issued_licence_recent",
                table: "issued_licence",
                column: "issued_at_utc",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "ux_issued_licence_licence_id",
                table: "issued_licence",
                column: "licence_id",
                unique: true);

            // The rules the API checks, kept in the database too for anything that writes another way.
            migrationBuilder.Sql(@"
                ALTER TABLE issued_licence DROP CONSTRAINT IF EXISTS ck_issued_licence_name;
                ALTER TABLE issued_licence ADD CONSTRAINT ck_issued_licence_name CHECK (length(btrim(hospital_name)) > 0);

                -- It ends on a date or after a number of days, not both being unset by accident: either is fine, but a
                -- number of days is positive, and a cap on machines is positive.
                ALTER TABLE issued_licence DROP CONSTRAINT IF EXISTS ck_issued_licence_limits;
                ALTER TABLE issued_licence ADD CONSTRAINT ck_issued_licence_limits
                    CHECK ((duration_days IS NULL OR duration_days > 0) AND (max_equipment IS NULL OR max_equipment > 0));

                -- The counter only has meaning once a code has been made: zero means none, and a locked licence has had one.
                ALTER TABLE issued_licence DROP CONSTRAINT IF EXISTS ck_issued_licence_lock;
                ALTER TABLE issued_licence ADD CONSTRAINT ck_issued_licence_lock
                    CHECK (lock_sequence >= 0 AND (NOT is_locked OR lock_sequence > 0));");

            // The counter that stops an old code undoing a newer one must never go backwards, whatever writes to the row.
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION fn_issued_licence_sequence_only_rises() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                BEGIN
                    IF NEW.lock_sequence < OLD.lock_sequence THEN
                        RAISE EXCEPTION 'A licence''s lock counter can only go up.'
                            USING ERRCODE = 'check_violation';
                    END IF;
                    RETURN NEW;
                END;
                $fn$;

                DROP TRIGGER IF EXISTS trg_issued_licence_sequence_only_rises ON issued_licence;
                CREATE TRIGGER trg_issued_licence_sequence_only_rises
                    BEFORE UPDATE ON issued_licence
                    FOR EACH ROW EXECUTE FUNCTION fn_issued_licence_sequence_only_rises();");

            // Audit is written by Postgres, never by application code. Who issued a licence, and who locked or
            // unlocked an installation, is a record the business has to be able to account for.
            migrationBuilder.Sql(@"
                DROP TRIGGER IF EXISTS trg_issued_licence_audit ON issued_licence;
                CREATE TRIGGER trg_issued_licence_audit
                    AFTER INSERT OR UPDATE OR DELETE ON issued_licence
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "issued_licence");
        }
    }
}
