using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Collapses four roles into two.
    ///
    /// Admin, BiomedicalHead, SeniorEngineer and Technician modelled a large
    /// teaching hospital. The departments this is sold to are usually one head
    /// and three technicians, and the middle two tiers meant an administrator
    /// choosing between near-identical options every time they added someone.
    ///
    /// Everyone keeps at least what they had. BiomedicalHead and SeniorEngineer
    /// both held rights that are now Admin's - scheduling, assigning, editing
    /// the register - so they become Admin rather than losing access silently,
    /// which is the migration failure that would strand a hospital's head of
    /// department outside their own system. Technician becomes Employee.
    ///
    /// No model change, so the snapshot is untouched: roles are rows, not
    /// schema.
    /// </summary>
    public partial class TwoRoles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Employee first, so the remap below has somewhere to send people.
            migrationBuilder.Sql(@"
                INSERT INTO app_role (tenant_id, name, normalized_name, description, concurrency_stamp)
                VALUES (1, 'Employee', 'EMPLOYEE',
                        'Works the floor: PM rounds, faults, and reading the register.',
                        gen_random_uuid()::text)
                ON CONFLICT DO NOTHING;");

            // Re-point every assignment before the old rows are removed.
            // Written as set operations rather than row by row: a hospital has
            // tens of staff, but a half-applied role migration leaves people
            // unable to sign in to anything, and one statement either applies
            // or does not.
            migrationBuilder.Sql(@"
                UPDATE app_user_role ur
                SET role_id = (SELECT id FROM app_role WHERE name = 'Admin')
                WHERE role_id IN (SELECT id FROM app_role WHERE name IN ('BiomedicalHead', 'SeniorEngineer'))
                  AND NOT EXISTS (
                      SELECT 1 FROM app_user_role existing
                      WHERE existing.user_id = ur.user_id
                        AND existing.role_id = (SELECT id FROM app_role WHERE name = 'Admin'));");

            migrationBuilder.Sql(@"
                UPDATE app_user_role ur
                SET role_id = (SELECT id FROM app_role WHERE name = 'Employee')
                WHERE role_id IN (SELECT id FROM app_role WHERE name = 'Technician')
                  AND NOT EXISTS (
                      SELECT 1 FROM app_user_role existing
                      WHERE existing.user_id = ur.user_id
                        AND existing.role_id = (SELECT id FROM app_role WHERE name = 'Employee'));");

            // Anyone who already held Admin as well as a middle role now has a
            // duplicate that the update above deliberately skipped rather than
            // collide with the primary key. Remove the leftovers.
            migrationBuilder.Sql(@"
                DELETE FROM app_user_role
                WHERE role_id IN (
                    SELECT id FROM app_role
                    WHERE name IN ('BiomedicalHead', 'SeniorEngineer', 'Technician'));");

            migrationBuilder.Sql(@"
                DELETE FROM app_role
                WHERE name IN ('BiomedicalHead', 'SeniorEngineer', 'Technician');");

            // Admin's description predates this and described it as one tier of
            // four. It is now one of two, and it is what the Staff page shows.
            migrationBuilder.Sql(@"
                UPDATE app_role
                SET description = 'Full access: the register, schedules, checklists, staff, backups and the licence.'
                WHERE name = 'Admin';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => throw new NotSupportedException(
                "Migrations are forward-only. Recovery is restore-from-backup, not a down-migration.");
    }
}
