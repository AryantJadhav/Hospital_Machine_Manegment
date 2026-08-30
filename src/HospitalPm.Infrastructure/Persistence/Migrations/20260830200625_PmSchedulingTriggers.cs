using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations;

/// <summary>
/// Integrity rules for PM scheduling, plus audit coverage.
///
/// The compliance value of this whole feature rests on a completed PM being
/// a fact that cannot be quietly edited afterwards, so the rules live in the
/// database rather than in whichever code path happens to touch a task.
/// </summary>
public partial class PmSchedulingTriggers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ---------------------------------------------------------------
        // A custom frequency needs an interval; a named one must not carry
        // a stray value that looks meaningful and is silently ignored.
        // ---------------------------------------------------------------
        migrationBuilder.Sql(@"
            ALTER TABLE pm_schedule DROP CONSTRAINT IF EXISTS ck_pm_schedule_interval;
            ALTER TABLE pm_schedule ADD CONSTRAINT ck_pm_schedule_interval
                CHECK ((frequency = 90 AND interval_days >= 1)
                    OR (frequency <> 90 AND interval_days = 0));");

        migrationBuilder.Sql(@"
            ALTER TABLE pm_schedule DROP CONSTRAINT IF EXISTS ck_pm_schedule_grace;
            ALTER TABLE pm_schedule ADD CONSTRAINT ck_pm_schedule_grace
                CHECK (grace_days >= 0 AND grace_days <= 90);");

        // ---------------------------------------------------------------
        // A completed PM is evidence. Once recorded it may not be reopened,
        // have its completion time moved, or have the checklist version it
        // was filled under swapped for a different one.
        //
        // Without this, "who signed off this PM and against which questions"
        // becomes editable, and the audit pack stops meaning anything.
        // ---------------------------------------------------------------
        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_pm_task_completion_is_final() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                -- 40 = Completed, 50 = Skipped
                IF OLD.status NOT IN (40, 50) THEN
                    RETURN NEW;
                END IF;

                IF NEW.status IS DISTINCT FROM OLD.status THEN
                    RAISE EXCEPTION
                        'PM task % is already closed and its outcome cannot be changed', OLD.id
                        USING ERRCODE = 'check_violation';
                END IF;

                IF NEW.completed_at_utc IS DISTINCT FROM OLD.completed_at_utc
                   OR NEW.completed_by_user_id IS DISTINCT FROM OLD.completed_by_user_id
                   OR NEW.checklist_template_version_id IS DISTINCT FROM OLD.checklist_template_version_id
                   OR NEW.due_date IS DISTINCT FROM OLD.due_date THEN
                    RAISE EXCEPTION
                        'PM task % is closed; its completion record is fixed', OLD.id
                        USING ERRCODE = 'check_violation';
                END IF;

                RETURN NEW;
            END;
            $fn$;");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_pm_task_completion_final ON pm_task;
            CREATE TRIGGER trg_pm_task_completion_final
                BEFORE UPDATE ON pm_task
                FOR EACH ROW EXECUTE FUNCTION fn_pm_task_completion_is_final();");

        // A completed task cannot be deleted. Deleting it would erase the PM
        // from the compliance record entirely, which is worse than editing it.
        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_pm_task_no_delete_completed() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                IF OLD.status IN (40, 50) THEN
                    RAISE EXCEPTION
                        'PM task % is closed and cannot be deleted', OLD.id
                        USING ERRCODE = 'check_violation';
                END IF;

                RETURN OLD;
            END;
            $fn$;");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_pm_task_no_delete_completed ON pm_task;
            CREATE TRIGGER trg_pm_task_no_delete_completed
                BEFORE DELETE ON pm_task
                FOR EACH ROW EXECUTE FUNCTION fn_pm_task_no_delete_completed();");

        // A completed task must say who did it, when, and against which
        // checklist version. A completion missing any of those is not
        // evidence, and is the shape a partially-written record would take.
        migrationBuilder.Sql(@"
            ALTER TABLE pm_task DROP CONSTRAINT IF EXISTS ck_pm_task_completion_complete;
            ALTER TABLE pm_task ADD CONSTRAINT ck_pm_task_completion_complete
                CHECK (status <> 40 OR (completed_at_utc IS NOT NULL
                                    AND completed_by_user_id IS NOT NULL
                                    AND checklist_template_version_id IS NOT NULL));");

        // A skip must carry a reason. 'Not done' with no explanation is the
        // single most useless row a compliance report can contain.
        migrationBuilder.Sql(@"
            ALTER TABLE pm_task DROP CONSTRAINT IF EXISTS ck_pm_task_skip_reason;
            ALTER TABLE pm_task ADD CONSTRAINT ck_pm_task_skip_reason
                CHECK (status <> 50 OR (skip_reason IS NOT NULL AND length(btrim(skip_reason)) > 0));");

        // ---------------------------------------------------------------
        // Audit and updated_at, same as every other table.
        // ---------------------------------------------------------------
        foreach (var table in new[] { "pm_schedule", "pm_task" })
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

        // The work list is "what is due or overdue, soonest first", and the
        // dashboard runs it constantly.
        migrationBuilder.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_pm_task_open
                ON pm_task (due_date)
                WHERE status IN (10, 20, 30);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException(
            "Migrations are forward-only. Recovery is restore-from-backup, not a down-migration.");
}
