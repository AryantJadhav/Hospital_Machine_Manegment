using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations;

/// <summary>
/// Append-only audit log written entirely by Postgres triggers.
///
/// Application code must never insert audit rows. An audit trail the
/// application can choose not to write is not an audit trail, so this is
/// enforced in the database rather than by convention.
/// </summary>
public partial class AuditInfrastructure : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
            CREATE TABLE IF NOT EXISTS audit_log (
                id             bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                tenant_id      integer      NOT NULL DEFAULT 1,
                table_name     text         NOT NULL,
                record_pk      text         NOT NULL,
                operation      text         NOT NULL
                                 CHECK (operation IN ('INSERT','UPDATE','DELETE')),
                old_data       jsonb,
                new_data       jsonb,
                changed_at_utc timestamptz  NOT NULL DEFAULT (now() AT TIME ZONE 'utc'),
                changed_by     text
            );");

        migrationBuilder.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_audit_log_table_record
                ON audit_log (tenant_id, table_name, record_pk);");

        migrationBuilder.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_audit_log_changed_at
                ON audit_log (changed_at_utc DESC);");

        // changed_by comes from a per-connection session variable the app sets
        // (SET LOCAL app.user_id). Read with missing_ok = true so an
        // unattributed write is still audited rather than rejected: losing the
        // actor is bad, losing the row entirely is worse.
        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_audit_row() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            DECLARE
                v_actor text := current_setting('app.user_id', true);
            BEGIN
                IF (TG_OP = 'DELETE') THEN
                    INSERT INTO audit_log (tenant_id, table_name, record_pk, operation, old_data, new_data, changed_by)
                    VALUES (COALESCE((to_jsonb(OLD) ->> 'tenant_id')::int, 1),
                            TG_TABLE_NAME,
                            COALESCE(to_jsonb(OLD) ->> 'id', ''),
                            'DELETE', to_jsonb(OLD), NULL, v_actor);
                    RETURN OLD;
                ELSIF (TG_OP = 'UPDATE') THEN
                    INSERT INTO audit_log (tenant_id, table_name, record_pk, operation, old_data, new_data, changed_by)
                    VALUES (COALESCE((to_jsonb(NEW) ->> 'tenant_id')::int, 1),
                            TG_TABLE_NAME,
                            COALESCE(to_jsonb(NEW) ->> 'id', ''),
                            'UPDATE', to_jsonb(OLD), to_jsonb(NEW), v_actor);
                    RETURN NEW;
                ELSE
                    INSERT INTO audit_log (tenant_id, table_name, record_pk, operation, old_data, new_data, changed_by)
                    VALUES (COALESCE((to_jsonb(NEW) ->> 'tenant_id')::int, 1),
                            TG_TABLE_NAME,
                            COALESCE(to_jsonb(NEW) ->> 'id', ''),
                            'INSERT', NULL, to_jsonb(NEW), v_actor);
                    RETURN NEW;
                END IF;
            END;
            $fn$;");

        // REVOKE alone is not enough: the app may connect as the table owner,
        // and owners bypass table privileges. A trigger that raises cannot be
        // bypassed without deliberately disabling it, which is a visible act.
        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_audit_log_is_append_only() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                RAISE EXCEPTION 'audit_log is append-only; % is not permitted', TG_OP
                    USING ERRCODE = 'check_violation';
            END;
            $fn$;");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_audit_log_append_only ON audit_log;
            CREATE TRIGGER trg_audit_log_append_only
                BEFORE UPDATE OR DELETE OR TRUNCATE ON audit_log
                FOR EACH STATEMENT EXECUTE FUNCTION fn_audit_log_is_append_only();");

        migrationBuilder.Sql(@"REVOKE UPDATE, DELETE, TRUNCATE ON audit_log FROM PUBLIC;");

        // updated_at maintained by the database so application code cannot
        // forget it.
        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_set_updated_at() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                NEW.updated_at_utc := (now() AT TIME ZONE 'utc');
                RETURN NEW;
            END;
            $fn$;");

        foreach (var table in new[] { "category", "equipment_type", "equipment_type_category" })
        {
            migrationBuilder.Sql($@"
                DROP TRIGGER IF EXISTS trg_{table}_audit ON {table};
                CREATE TRIGGER trg_{table}_audit
                    AFTER INSERT OR UPDATE OR DELETE ON {table}
                    FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");
        }

        foreach (var table in new[] { "category", "equipment_type" })
        {
            migrationBuilder.Sql($@"
                DROP TRIGGER IF EXISTS trg_{table}_updated_at ON {table};
                CREATE TRIGGER trg_{table}_updated_at
                    BEFORE UPDATE ON {table}
                    FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();");
        }

        // At most one primary category per equipment type. "At least one"
        // cannot be a row constraint (the type row exists before any link
        // row does); that half is enforced by the application and covered
        // by tests.
        migrationBuilder.Sql(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ux_equipment_type_one_primary_category
                ON equipment_type_category (equipment_type_id)
                WHERE is_primary;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException(
            "Migrations are forward-only. Recovery is restore-from-backup, not a down-migration.");
}
