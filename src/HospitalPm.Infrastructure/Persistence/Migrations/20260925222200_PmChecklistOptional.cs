using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PmChecklistOptional : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A PM does not need a checklist. Idempotent, like the other schema changes.
            migrationBuilder.Sql(@"
                -- A schedule can have no checklist, and so can the completion of one.
                ALTER TABLE pm_schedule ALTER COLUMN checklist_template_id DROP NOT NULL;
                ALTER TABLE pm_completion ALTER COLUMN checklist_template_version_id DROP NOT NULL;

                -- A completed PM still has to say when and by whom. It no longer has to name a
                -- checklist version, because it may have had no checklist.
                ALTER TABLE pm_task DROP CONSTRAINT IF EXISTS ck_pm_task_completion_complete;
                ALTER TABLE pm_task ADD CONSTRAINT ck_pm_task_completion_complete
                    CHECK (status <> 40 OR (completed_at_utc IS NOT NULL AND completed_by_user_id IS NOT NULL));

                -- The unique index on (machine, checklist) sees every empty checklist as different,
                -- so one schedule with no checklist per machine for each of who does it, the team or
                -- the vendor, is enforced here.
                CREATE UNIQUE INDEX IF NOT EXISTS ux_pm_schedule_equipment_plain
                    ON pm_schedule (equipment_id, performed_by)
                    WHERE checklist_template_id IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
