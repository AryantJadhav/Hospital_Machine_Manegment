using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditRecordsWho : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The audit function now reads the signed-in person from the session setting the application
            // fills in on every connection (see AuditActorInterceptor), and records nothing at all rather
            // than an empty text when there was no one: a change made by the system's own jobs is then
            // plainly "not by a person", not a blank. Everything else about it, including the removal of passwords and other secrets before a row is written, is as it was.
            //
            // Rows written before this carry no person, because nothing told the database who it was.
            // They are not rewritten (the log is append-only) and the audit page says so.
            migrationBuilder.Sql(@"
                CREATE OR REPLACE FUNCTION fn_audit_row() RETURNS trigger
                LANGUAGE plpgsql AS $fn$
                DECLARE
                    v_actor   text := NULLIF(current_setting('app.user_id', true), '');
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
