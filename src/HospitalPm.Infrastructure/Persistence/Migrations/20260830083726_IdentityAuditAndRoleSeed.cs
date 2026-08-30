using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations;

/// <summary>
/// Extends the audit trail to the identity tables, and seeds the four roles.
///
/// The generic audit trigger captures whole rows as jsonb. Applied naively to
/// app_user that would copy every password hash and security stamp into
/// audit_log — a table that is deliberately append-only and can never be
/// cleaned up. This migration redacts those columns at the trigger, before
/// the row is ever written.
/// </summary>
public partial class IdentityAuditAndRoleSeed : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ---------------------------------------------------------------
        // Redact secrets before they reach the audit trail.
        //
        // audit_log is append-only by design, so a secret written into it
        // cannot be deleted later — the fix has to happen before the insert,
        // not after. jsonb minus a text[] drops every listed key; keys that
        // are not present are ignored, so one list works for all tables.
        // ---------------------------------------------------------------
        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_audit_row() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            DECLARE
                v_actor   text := current_setting('app.user_id', true);
                v_secrets text[] := ARRAY[
                    'password_hash',
                    'security_stamp',
                    'concurrency_stamp',
                    'token_hash',
                    'value'
                ];
                v_old jsonb;
                v_new jsonb;
            BEGIN
                IF (TG_OP <> 'INSERT') THEN
                    v_old := to_jsonb(OLD) - v_secrets;
                END IF;
                IF (TG_OP <> 'DELETE') THEN
                    v_new := to_jsonb(NEW) - v_secrets;
                END IF;

                IF (TG_OP = 'DELETE') THEN
                    INSERT INTO audit_log (tenant_id, table_name, record_pk, operation, old_data, new_data, changed_by)
                    VALUES (COALESCE((to_jsonb(OLD) ->> 'tenant_id')::int, 1),
                            TG_TABLE_NAME, COALESCE(to_jsonb(OLD) ->> 'id', ''),
                            'DELETE', v_old, NULL, v_actor);
                    RETURN OLD;
                ELSIF (TG_OP = 'UPDATE') THEN
                    INSERT INTO audit_log (tenant_id, table_name, record_pk, operation, old_data, new_data, changed_by)
                    VALUES (COALESCE((to_jsonb(NEW) ->> 'tenant_id')::int, 1),
                            TG_TABLE_NAME, COALESCE(to_jsonb(NEW) ->> 'id', ''),
                            'UPDATE', v_old, v_new, v_actor);
                    RETURN NEW;
                ELSE
                    INSERT INTO audit_log (tenant_id, table_name, record_pk, operation, old_data, new_data, changed_by)
                    VALUES (COALESCE((to_jsonb(NEW) ->> 'tenant_id')::int, 1),
                            TG_TABLE_NAME, COALESCE(to_jsonb(NEW) ->> 'id', ''),
                            'INSERT', NULL, v_new, v_actor);
                    RETURN NEW;
                END IF;
            END;
            $fn$;");

        // ---------------------------------------------------------------
        // Audit the identity tables.
        //
        // Who was granted which role, and when, is the question a NABH
        // auditor actually asks. app_user_role matters as much as app_user.
        // ---------------------------------------------------------------
        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_app_user_audit ON app_user;
            CREATE TRIGGER trg_app_user_audit
                AFTER INSERT OR UPDATE OR DELETE ON app_user
                FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_app_role_audit ON app_role;
            CREATE TRIGGER trg_app_role_audit
                AFTER INSERT OR UPDATE OR DELETE ON app_role
                FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");

        // app_user_role has a composite key and no id column; the generic
        // function falls back to an empty record_pk, so the user and role
        // ids are recoverable from new_data/old_data instead.
        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_app_user_role_audit ON app_user_role;
            CREATE TRIGGER trg_app_user_role_audit
                AFTER INSERT OR UPDATE OR DELETE ON app_user_role
                FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_app_user_updated_at ON app_user;
            CREATE TRIGGER trg_app_user_updated_at
                BEFORE UPDATE ON app_user
                FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();");

        // ---------------------------------------------------------------
        // Seed the four roles.
        //
        // Normalized name is what Identity looks up on; seeding it wrong
        // makes role checks silently fail rather than error.
        // ---------------------------------------------------------------
        migrationBuilder.Sql(@"
            INSERT INTO app_role (tenant_id, name, normalized_name, description, concurrency_stamp)
            VALUES
                (1, 'Admin',          'ADMIN',          'Full access including user management and licence.', gen_random_uuid()::text),
                (1, 'BiomedicalHead', 'BIOMEDICALHEAD', 'Owns the equipment register, schedules and compliance reporting.', gen_random_uuid()::text),
                (1, 'SeniorEngineer', 'SENIORENGINEER', 'Assigns and closes work orders; approves completed PMs.', gen_random_uuid()::text),
                (1, 'Technician',     'TECHNICIAN',     'Executes PMs and breakdown jobs on the floor.', gen_random_uuid()::text)
            ON CONFLICT DO NOTHING;");

        // Refresh tokens are looked up by hash on every refresh. An expired
        // or revoked token still occupies the unique index, so a cleanup job
        // needs this to find them cheaply.
        migrationBuilder.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_refresh_token_expired
                ON refresh_token (expires_at_utc)
                WHERE revoked_at_utc IS NULL;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException(
            "Migrations are forward-only. Recovery is restore-from-backup, not a down-migration.");
}
