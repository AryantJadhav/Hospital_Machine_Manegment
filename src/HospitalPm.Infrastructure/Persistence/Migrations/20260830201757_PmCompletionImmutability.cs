using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations;

/// <summary>
/// A filled-in checklist is evidence and cannot be revised.
///
/// This is the record a NABH auditor is shown: these readings, this
/// signature, against these questions, on this date. If any of it can be
/// edited afterwards, none of it proves anything — and the edit would be
/// invisible, because a revised record looks exactly as consistent as an
/// honest one.
///
/// A mistake is corrected by doing the PM again, which leaves both records
/// standing. That is the point.
/// </summary>
public partial class PmCompletionImmutability : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_pm_completion_immutable() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                RAISE EXCEPTION
                    'PM completion % is a signed record and cannot be %; repeat the PM instead',
                    OLD.id,
                    CASE TG_OP WHEN 'UPDATE' THEN 'changed' ELSE 'deleted' END
                    USING ERRCODE = 'check_violation';
            END;
            $fn$;");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_pm_completion_immutable ON pm_completion;
            CREATE TRIGGER trg_pm_completion_immutable
                BEFORE UPDATE OR DELETE ON pm_completion
                FOR EACH ROW EXECUTE FUNCTION fn_pm_completion_immutable();");

        // A completion must name who signed it and when the work was done.
        // A record missing either is not evidence.
        migrationBuilder.Sql(@"
            ALTER TABLE pm_completion DROP CONSTRAINT IF EXISTS ck_pm_completion_signed;
            ALTER TABLE pm_completion ADD CONSTRAINT ck_pm_completion_signed
                CHECK (completed_by_user_id IS NOT NULL AND completed_at_utc IS NOT NULL);");

        // Signature images are drawn on a phone; a megabyte means something
        // other than a signature arrived.
        migrationBuilder.Sql(@"
            ALTER TABLE pm_completion DROP CONSTRAINT IF EXISTS ck_pm_completion_signature_size;
            ALTER TABLE pm_completion ADD CONSTRAINT ck_pm_completion_signature_size
                CHECK (signature_png IS NULL OR length(signature_png) <= 1048576);");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_pm_completion_audit ON pm_completion;
            CREATE TRIGGER trg_pm_completion_audit
                AFTER INSERT OR UPDATE OR DELETE ON pm_completion
                FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");

        // Equipment history is "every completion for this machine, newest
        // first" — the query a technician runs after scanning a tag.
        migrationBuilder.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_pm_completion_completed_at
                ON pm_completion (completed_at_utc DESC);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException(
            "Migrations are forward-only. Recovery is restore-from-backup, not a down-migration.");
}
