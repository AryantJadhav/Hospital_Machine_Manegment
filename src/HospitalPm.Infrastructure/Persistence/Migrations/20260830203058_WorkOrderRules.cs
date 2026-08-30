using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations;

/// <summary>
/// Work order numbering and the state machine.
///
/// The transition table is mirrored from WorkOrderTransitions in the domain.
/// Two copies is a real cost, accepted deliberately: the application decides
/// what to offer a user, and the database decides what is possible at all.
/// Bulk imports, an admin at a psql prompt, and any future service all go
/// through the second one.
/// </summary>
public partial class WorkOrderRules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ---------------------------------------------------------------
        // Numbering.
        //
        // A sequence, not a count-and-increment, because two wards reporting
        // a fault in the same second would otherwise race for the same
        // number. Not reset per year: resetting needs a read-then-write that
        // races, and uniqueness matters more than cosmetics.
        // ---------------------------------------------------------------
        migrationBuilder.Sql("CREATE SEQUENCE IF NOT EXISTS work_order_number_seq START 1;");

        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_work_order_number() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                IF NEW.number IS NULL OR btrim(NEW.number) = '' THEN
                    NEW.number := 'WO-'
                        || to_char(COALESCE(NEW.reported_at_utc, now()), 'YYYY')
                        || '-'
                        || lpad(nextval('work_order_number_seq')::text, 6, '0');
                END IF;

                RETURN NEW;
            END;
            $fn$;");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_work_order_number ON work_order;
            CREATE TRIGGER trg_work_order_number
                BEFORE INSERT ON work_order
                FOR EACH ROW EXECUTE FUNCTION fn_work_order_number();");

        // ---------------------------------------------------------------
        // State machine.
        // ---------------------------------------------------------------
        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_work_order_transition() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            DECLARE
                ok boolean := false;
            BEGIN
                IF NEW.status = OLD.status THEN
                    RETURN NEW;
                END IF;

                -- 10 Reported, 20 Assigned, 30 InProgress, 40 OnHold,
                -- 50 Resolved, 60 Closed, 70 Cancelled
                ok := CASE OLD.status
                    WHEN 10 THEN NEW.status IN (20, 30, 70)
                    WHEN 20 THEN NEW.status IN (30, 10, 40, 70)
                    WHEN 30 THEN NEW.status IN (50, 40, 70)
                    WHEN 40 THEN NEW.status IN (30, 20, 70)
                    -- Resolved may reopen: 'we thought it was fixed' is the
                    -- commonest thing that happens to a resolved ticket.
                    WHEN 50 THEN NEW.status IN (60, 30)
                    ELSE false
                END;

                IF NOT ok THEN
                    RAISE EXCEPTION
                        'work order % cannot move from status % to status %',
                        OLD.number, OLD.status, NEW.status
                        USING ERRCODE = 'check_violation';
                END IF;

                RETURN NEW;
            END;
            $fn$;");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_work_order_transition ON work_order;
            CREATE TRIGGER trg_work_order_transition
                BEFORE UPDATE OF status ON work_order
                FOR EACH ROW EXECUTE FUNCTION fn_work_order_transition();");

        // ---------------------------------------------------------------
        // A closed ticket is a record of what happened.
        //
        // Reopening is allowed from Resolved, so this only freezes Closed and
        // Cancelled. Rewriting a resolution after the fact would make the
        // repair history unreliable in exactly the way the PM records are
        // protected from.
        // ---------------------------------------------------------------
        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_work_order_terminal_is_final() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                IF OLD.status NOT IN (60, 70) THEN
                    RETURN NEW;
                END IF;

                IF NEW.resolution_notes IS DISTINCT FROM OLD.resolution_notes
                   OR NEW.resolved_by_user_id IS DISTINCT FROM OLD.resolved_by_user_id
                   OR NEW.resolved_at_utc IS DISTINCT FROM OLD.resolved_at_utc
                   OR NEW.fault_description IS DISTINCT FROM OLD.fault_description
                   OR NEW.equipment_id IS DISTINCT FROM OLD.equipment_id THEN
                    RAISE EXCEPTION
                        'work order % is closed; its record cannot be rewritten', OLD.number
                        USING ERRCODE = 'check_violation';
                END IF;

                RETURN NEW;
            END;
            $fn$;");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_work_order_terminal_final ON work_order;
            CREATE TRIGGER trg_work_order_terminal_final
                BEFORE UPDATE ON work_order
                FOR EACH ROW EXECUTE FUNCTION fn_work_order_terminal_is_final();");

        // A resolved ticket must say what was done. "Fixed" with no
        // explanation teaches the next engineer nothing and is worthless in
        // a recurring-fault review.
        migrationBuilder.Sql(@"
            ALTER TABLE work_order DROP CONSTRAINT IF EXISTS ck_work_order_resolution;
            ALTER TABLE work_order ADD CONSTRAINT ck_work_order_resolution
                CHECK (status NOT IN (50, 60)
                    OR (resolution_notes IS NOT NULL
                        AND length(btrim(resolution_notes)) > 0
                        AND resolved_by_user_id IS NOT NULL
                        AND resolved_at_utc IS NOT NULL));");

        // Downtime must not run backwards.
        migrationBuilder.Sql(@"
            ALTER TABLE work_order DROP CONSTRAINT IF EXISTS ck_work_order_downtime;
            ALTER TABLE work_order ADD CONSTRAINT ck_work_order_downtime
                CHECK (back_in_service_at_utc IS NULL
                    OR out_of_service_at_utc IS NULL
                    OR back_in_service_at_utc >= out_of_service_at_utc);");

        // Notes are a timeline, not a document. Editing one rewrites a
        // conversation that other people acted on.
        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_work_order_note_append_only() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                RAISE EXCEPTION 'work order notes are append-only; % is not permitted', TG_OP
                    USING ERRCODE = 'check_violation';
            END;
            $fn$;");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_work_order_note_append_only ON work_order_note;
            CREATE TRIGGER trg_work_order_note_append_only
                BEFORE UPDATE OR DELETE ON work_order_note
                FOR EACH ROW EXECUTE FUNCTION fn_work_order_note_append_only();");

        foreach (var table in new[] { "work_order", "work_order_note" })
        {
            migrationBuilder.Sql($@"
                DROP TRIGGER IF EXISTS trg_{table}_audit ON {table};
                CREATE TRIGGER trg_{table}_audit
                    AFTER INSERT OR UPDATE OR DELETE ON {table}
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_work_order_updated_at ON work_order;
            CREATE TRIGGER trg_work_order_updated_at
                BEFORE UPDATE ON work_order
                FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();");

        // The open queue, worst first — the list an engineer works from.
        migrationBuilder.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_work_order_open
                ON work_order (priority DESC, reported_at_utc)
                WHERE status IN (10, 20, 30, 40);");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException(
            "Migrations are forward-only. Recovery is restore-from-backup, not a down-migration.");
}
