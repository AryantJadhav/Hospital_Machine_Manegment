using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations;

/// <summary>
/// Integrity rules for the location tree, plus audit coverage for the two
/// new tables.
///
/// The materialised path is maintained by the database, not by application
/// code. Excel import writes locations in bulk and a future admin will run
/// direct SQL to fix a hospital's tree; either could leave the path wrong,
/// and a wrong path silently returns the wrong equipment for a whole site.
/// </summary>
public partial class LocationPathTriggers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // ---------------------------------------------------------------
        // A child must sit at a deeper level than its parent.
        //
        // Levels may be skipped — a nursing home goes Site straight to
        // Department with no Building or Floor — but they may not invert or
        // repeat, or "everything under this site" stops meaning anything.
        // ---------------------------------------------------------------
        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_location_check_level() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            DECLARE
                v_parent_level int;
            BEGIN
                IF NEW.parent_id IS NULL THEN
                    RETURN NEW;
                END IF;

                SELECT level INTO v_parent_level FROM location WHERE id = NEW.parent_id;

                IF v_parent_level IS NULL THEN
                    RAISE EXCEPTION 'parent location % does not exist', NEW.parent_id
                        USING ERRCODE = 'foreign_key_violation';
                END IF;

                IF NEW.level <= v_parent_level THEN
                    RAISE EXCEPTION
                        'location level % must be deeper than its parent level %',
                        NEW.level, v_parent_level
                        USING ERRCODE = 'check_violation';
                END IF;

                RETURN NEW;
            END;
            $fn$;");

        // ---------------------------------------------------------------
        // Maintain path and depth, and refuse cycles.
        //
        // BEFORE INSERT works here because Postgres evaluates column
        // defaults — including the identity sequence — before row-level
        // BEFORE triggers run, so NEW.id is already populated. Setting the
        // path in one BEFORE trigger avoids an AFTER-insert UPDATE, which
        // would fire the reparent trigger below with an empty OLD.path and
        // match every row in the table.
        //
        // Without the cycle check, one bad parent_id turns every recursive
        // query into an infinite loop and takes the service down.
        // ---------------------------------------------------------------
        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_location_set_path() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            DECLARE
                v_parent_path  text;
                v_parent_depth int;
            BEGIN
                IF NEW.parent_id IS NULL THEN
                    NEW.path  := '/' || NEW.id || '/';
                    NEW.depth := 0;
                ELSE
                    IF NEW.parent_id = NEW.id THEN
                        RAISE EXCEPTION 'location % cannot be its own parent', NEW.id
                            USING ERRCODE = 'check_violation';
                    END IF;

                    SELECT path, depth INTO v_parent_path, v_parent_depth
                    FROM location WHERE id = NEW.parent_id;

                    IF v_parent_path IS NULL THEN
                        RAISE EXCEPTION 'parent location % does not exist', NEW.parent_id
                            USING ERRCODE = 'foreign_key_violation';
                    END IF;

                    -- Re-parenting a node underneath its own descendant
                    -- would detach the whole subtree from the tree.
                    IF v_parent_path LIKE '%/' || NEW.id || '/%' THEN
                        RAISE EXCEPTION 'location % cannot be its own ancestor', NEW.id
                            USING ERRCODE = 'check_violation';
                    END IF;

                    NEW.path  := v_parent_path || NEW.id || '/';
                    NEW.depth := v_parent_depth + 1;
                END IF;

                RETURN NEW;
            END;
            $fn$;");

        // Moving a subtree must move every descendant's path with it,
        // otherwise the children silently detach from the new location and a
        // subtree search quietly misses them.
        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_location_reparent_descendants() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN
                -- Guard against an empty or null OLD.path. Without it the
                -- LIKE pattern degenerates to '_%', which matches every row
                -- in the table and rewrites paths that were never involved.
                IF OLD.path IS NULL OR OLD.path = '' THEN
                    RETURN NULL;
                END IF;

                IF NEW.path IS DISTINCT FROM OLD.path THEN
                    UPDATE location
                    SET path  = NEW.path || substring(path from length(OLD.path) + 1),
                        depth = NEW.depth + (depth - OLD.depth)
                    WHERE path LIKE OLD.path || '_%'
                      AND id <> NEW.id;
                END IF;

                RETURN NULL;
            END;
            $fn$;");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_location_check_level ON location;
            CREATE TRIGGER trg_location_check_level
                BEFORE INSERT OR UPDATE OF parent_id, level ON location
                FOR EACH ROW EXECUTE FUNCTION fn_location_check_level();");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_location_path_insert ON location;
            DROP TRIGGER IF EXISTS trg_location_path_update ON location;
            DROP TRIGGER IF EXISTS trg_location_path ON location;
            CREATE TRIGGER trg_location_path
                BEFORE INSERT OR UPDATE OF parent_id ON location
                FOR EACH ROW EXECUTE FUNCTION fn_location_set_path();");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_location_reparent ON location;
            -- AFTER UPDATE, not AFTER UPDATE OF path. Postgres matches
            -- 'UPDATE OF col' against the columns named in the statement's
            -- SET clause, not against columns a BEFORE trigger rewrote. EF
            -- sets parent_id, and the path is derived in fn_location_set_path,
            -- so an 'OF path' trigger would never fire and descendants would
            -- silently keep their old ancestor path.
            CREATE TRIGGER trg_location_reparent
                AFTER UPDATE ON location
                FOR EACH ROW EXECUTE FUNCTION fn_location_reparent_descendants();");

        // ---------------------------------------------------------------
        // Equipment may only sit at a place a machine can physically be.
        //
        // Attaching an asset to an Organisation or Site is how a register
        // becomes useless: a technician sent to "Apollo Hospitals" has not
        // been told anything. Building and below is the floor.
        // ---------------------------------------------------------------
        migrationBuilder.Sql(@"
            ALTER TABLE equipment DROP CONSTRAINT IF EXISTS ck_equipment_location_is_physical;");

        migrationBuilder.Sql(@"
            CREATE OR REPLACE FUNCTION fn_equipment_check_location() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            DECLARE
                v_level int;
            BEGIN
                SELECT level INTO v_level FROM location WHERE id = NEW.location_id;

                IF v_level IS NULL THEN
                    RAISE EXCEPTION 'location % does not exist', NEW.location_id
                        USING ERRCODE = 'foreign_key_violation';
                END IF;

                -- 30 = Building. Anything shallower is an administrative
                -- grouping, not a place you can walk to.
                IF v_level < 30 THEN
                    RAISE EXCEPTION
                        'equipment must be placed at building level or deeper, not level %', v_level
                        USING ERRCODE = 'check_violation';
                END IF;

                RETURN NEW;
            END;
            $fn$;");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_equipment_check_location ON equipment;
            CREATE TRIGGER trg_equipment_check_location
                BEFORE INSERT OR UPDATE OF location_id ON equipment
                FOR EACH ROW EXECUTE FUNCTION fn_equipment_check_location();");

        // ---------------------------------------------------------------
        // Audit and updated_at, same as every other table.
        // ---------------------------------------------------------------
        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_location_audit ON location;
            CREATE TRIGGER trg_location_audit
                AFTER INSERT OR UPDATE OR DELETE ON location
                FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_equipment_audit ON equipment;
            CREATE TRIGGER trg_equipment_audit
                AFTER INSERT OR UPDATE OR DELETE ON equipment
                FOR EACH ROW EXECUTE FUNCTION fn_audit_row();");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_location_updated_at ON location;
            CREATE TRIGGER trg_location_updated_at
                BEFORE UPDATE ON location
                FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();");

        migrationBuilder.Sql(@"
            DROP TRIGGER IF EXISTS trg_equipment_updated_at ON equipment;
            CREATE TRIGGER trg_equipment_updated_at
                BEFORE UPDATE ON equipment
                FOR EACH ROW EXECUTE FUNCTION fn_set_updated_at();");

        // Asset tag lookup from a QR scan is the single hottest query on the
        // mobile app; case-insensitive so a hand-typed tag still resolves.
        migrationBuilder.Sql(@"
            CREATE INDEX IF NOT EXISTS ix_equipment_asset_tag_lower
                ON equipment (tenant_id, lower(asset_tag));");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException(
            "Migrations are forward-only. Recovery is restore-from-backup, not a down-migration.");
}
