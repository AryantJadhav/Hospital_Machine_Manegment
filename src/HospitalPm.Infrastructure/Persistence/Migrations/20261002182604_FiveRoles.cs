using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Two roles become five: Developer, ItAdmin, BmeHead, BmeEngineer and DepartmentUser.
    ///
    /// Nobody loses access. The rows for Admin and Employee are renamed in place rather than
    /// replaced, so every account keeps its assignment: Admin becomes BmeHead, which holds
    /// everything Admin did, and Employee becomes BmeEngineer, which holds exactly what Employee did.
    ///
    /// One more thing is decided here. The person who first set the installation up, the
    /// earliest administrator, becomes the Developer. That is the account that can make the others
    /// (only a Developer makes a Developer, and the IT team and the head of department are made by
    /// someone above them), so an installation that is already running must have one or nobody
    /// could ever add the hospital's IT team. It is done only when there is no Developer yet, so
    /// running this twice, or on an installation that has one, changes nothing.
    ///
    /// No model change, so the snapshot is untouched: roles are rows, not schema.
    /// </summary>
    public partial class FiveRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Renamed in place. Normalized name is what Identity looks up on; getting it wrong makes role
            // checks fail silently rather than loudly.
            migrationBuilder.Sql(@"
                UPDATE app_role
                SET name = 'BmeHead',
                    normalized_name = 'BMEHEAD',
                    description = 'Head of Biomedical: the register, schedules, checklists, spare parts, training, reports and staff.'
                WHERE name = 'Admin'
                  AND NOT EXISTS (SELECT 1 FROM app_role WHERE name = 'BmeHead');");

            migrationBuilder.Sql(@"
                UPDATE app_role
                SET name = 'BmeEngineer',
                    normalized_name = 'BMEENGINEER',
                    description = 'Works the floor: PM rounds, faults, and reading the register.'
                WHERE name = 'Employee'
                  AND NOT EXISTS (SELECT 1 FROM app_role WHERE name = 'BmeEngineer');");

            migrationBuilder.Sql(@"
                INSERT INTO app_role (tenant_id, name, normalized_name, description, concurrency_stamp)
                VALUES
                    (1, 'Developer',      'DEVELOPER',      'Built and supports the software. Full access.', gen_random_uuid()::text),
                    (1, 'ItAdmin',        'ITADMIN',        'The hospital''s IT team: staff accounts, backups, updates, the licence and diagnostics.', gen_random_uuid()::text),
                    (1, 'DepartmentUser', 'DEPARTMENTUSER', 'Reports faults on the equipment of their own department.', gen_random_uuid()::text)
                ON CONFLICT DO NOTHING;");

            // The first administrator becomes the Developer, but only when there is not one already.
            migrationBuilder.Sql(@"
                UPDATE app_user_role
                SET role_id = (SELECT id FROM app_role WHERE name = 'Developer')
                WHERE role_id = (SELECT id FROM app_role WHERE name = 'BmeHead')
                  AND user_id = (
                      SELECT MIN(ur.user_id) FROM app_user_role ur
                      WHERE ur.role_id = (SELECT id FROM app_role WHERE name = 'BmeHead'))
                  AND NOT EXISTS (
                      SELECT 1 FROM app_user_role
                      WHERE role_id = (SELECT id FROM app_role WHERE name = 'Developer'));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => throw new NotSupportedException(
                "Migrations are forward-only. Recovery is restore-from-backup, not a down-migration.");
    }
}
