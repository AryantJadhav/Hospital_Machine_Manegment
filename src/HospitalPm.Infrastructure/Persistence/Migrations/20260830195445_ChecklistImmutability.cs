using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations;

/// <summary>
/// Makes a published checklist version physically unable to change.
///
/// This is the rule the whole design exists to protect. A PM completed in
/// 2026 must render exactly as the technician saw it when an auditor opens
/// it in 2031, even though the template has moved on several versions.
/// Editing a published definition in place would silently rewrite history,
/// and the rewrite would be invisible — the completion would still look
/// perfectly consistent.
///
/// Enforced in the database rather than in application code, for the same
/// reason the audit log is: a guarantee an auditor relies on should not
/// depend on every future code path remembering to check.
/// </summary>
public partial class ChecklistImmutability : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ---------------------------------------------------------------
        // A published or archived version is frozen.
        //
        // Status may still move forward (published -> archived) because
        // superseding a version is not editing it. Everything else about the
        // row is refused.
        // ---------------------------------------------------------------
        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_checklist_version_immutable() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                -- 10 = Draft. A draft is meant to be edited.
                IF OLD.status = 10 THEN
                    RETURN NEW;
                END IF;

                IF NEW.definition IS DISTINCT FROM OLD.definition THEN
                    RAISE EXCEPTION
                        'checklist version % is published and its questions cannot be changed; create a new version instead',
                        OLD.id
                        USING ERRCODE = 'check_violation';
                END IF;

                IF NEW.version_no IS DISTINCT FROM OLD.version_no
                   OR NEW.checklist_template_id IS DISTINCT FROM OLD.checklist_template_id
                   OR NEW.published_at_utc IS DISTINCT FROM OLD.published_at_utc THEN
                    RAISE EXCEPTION
                        'checklist version % is published; version number, template and publish time are fixed',
                        OLD.id
                        USING ERRCODE = 'check_violation';
                END IF;

                -- 20 = Published, 30 = Archived. Forward only.
                IF NEW.status < OLD.status THEN
                    RAISE EXCEPTION
                        'checklist version % cannot return to an earlier status', OLD.id
                        USING ERRCODE = 'check_violation';
                END IF;

                RETURN NEW;
            END;
            $fn$;");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_checklist_version_immutable ON checklist_template_version;
            CREATE TRIGGER trg_checklist_version_immutable
                BEFORE UPDATE ON checklist_template_version
                FOR EACH ROW EXECUTE FUNCTION fn_checklist_version_immutable();");

        // A published version cannot be deleted either. Completions will
        // reference it by foreign key, but the guard is added now so the
        // rule holds from the first published version rather than from
        // whenever the completions table arrives.
        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_checklist_version_no_delete() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                IF OLD.status <> 10 THEN
                    RAISE EXCEPTION
                        'checklist version % is published and cannot be deleted; archive it instead',
                        OLD.id
                        USING ERRCODE = 'check_violation';
                END IF;

                RETURN OLD;
            END;
            $fn$;");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_checklist_version_no_delete ON checklist_template_version;
            CREATE TRIGGER trg_checklist_version_no_delete
                BEFORE DELETE ON checklist_template_version
                FOR EACH ROW EXECUTE FUNCTION fn_checklist_version_no_delete();");

        // ---------------------------------------------------------------
        // At most one draft and one published version per template.
        //
        // Two published versions would make "which questions apply now"
        // ambiguous, and two drafts would let one author's edits silently
        // shadow another's.
        // ---------------------------------------------------------------
        migrationBuilder.Sql(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ux_checklist_one_draft
                ON checklist_template_version (checklist_template_id)
                WHERE status = 10;");

        migrationBuilder.Sql(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ux_checklist_one_published
                ON checklist_template_version (checklist_template_id)
                WHERE status = 20;");

        // Version numbers are unique within a template once assigned. Drafts
        // sit at 0 until publish, so they are excluded.
        migrationBuilder.Sql(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ux_checklist_version_no
                ON checklist_template_version (checklist_template_id, version_no)
                WHERE version_no > 0;");

        // ---------------------------------------------------------------
        // Audit and updated_at, same as every other table.
        // ---------------------------------------------------------------
        foreach (var table in new[] { "checklist_template", "checklist_template_version" })
        {
            migrationBuilder.Sql($@"
                DROP TRIGGER IF EXISTS trg_{table}_audit ON {table};
                CREATE TRIGGER trg_{table}_audit
                    AFTER INSERT OR UPDATE OR DELETE ON {table}
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");

            migrationBuilder.Sql($@"
                DROP TRIGGER IF EXISTS trg_{table}_updated_at ON {table};
                CREATE TRIGGER trg_{table}_updated_at
                    BEFORE UPDATE ON {table}
                    FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();");
        }

        // Finding the live checklist for an equipment type is the hottest
        // query once PM scheduling exists.
        migrationBuilder.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_checklist_version_published
                ON checklist_template_version (checklist_template_id)
                WHERE status = 20;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException(
            "Migrations are forward-only. Recovery is restore-from-backup, not a down-migration.");
}
