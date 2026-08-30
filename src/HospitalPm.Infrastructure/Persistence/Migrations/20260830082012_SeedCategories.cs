using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HospitalPm.Infrastructure.Persistence.Migrations;

/// <summary>
/// Seeds the fixed device-classification categories.
///
/// Idempotent: re-running matches on (tenant_id, code) and does nothing.
/// Codes are the stable machine key — renaming a category's display name is
/// safe, changing its code is not, because imports and saved reports key on it.
/// </summary>
public partial class SeedCategories : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(@"
            INSERT INTO category (tenant_id, code, name, description, display_order, is_active)
            VALUES
                (1, 'diagnostic', 'Diagnostic devices', 'Establish or support a diagnosis. Excludes imaging and laboratory analysers, which have their own categories.', 1, true),
                (1, 'therapeutic', 'Therapeutic devices', 'Deliver treatment directly to a patient.', 2, true),
                (1, 'monitoring', 'Monitoring devices', 'Continuously observe physiological parameters.', 3, true),
                (1, 'life-support', 'Life-support devices', 'Sustain a vital function; failure is immediately life-threatening.', 4, true),
                (1, 'surgical', 'Surgical devices', 'Used in the operating theatre during a procedure.', 5, true),
                (1, 'rehabilitation', 'Rehabilitation devices', 'Restore function in physiotherapy and rehabilitation.', 6, true),
                (1, 'assistive', 'Assistive devices', 'Aid patient mobility or daily function.', 7, true),
                (1, 'preventive', 'Preventive devices', 'Sterilisation, disinfection and infection control.', 8, true),
                (1, 'implantable', 'Implantable devices', 'Placed inside the body. See notes: rarely a maintainable hospital asset.', 9, true),
                (1, 'laboratory-diagnostic', 'Laboratory diagnostic devices', 'Analysers and instruments in the clinical laboratory.', 10, true),
                (1, 'imaging', 'Imaging devices', 'Produce diagnostic images. Radiation-emitting units carry regulatory licensing.', 11, true),
                (1, 'drug-delivery', 'Drug-delivery devices', 'Administer medication at a controlled rate or dose.', 12, true),
                (1, 'patient-care', 'Patient-care devices', 'General ward and bedside equipment.', 13, true),
                (1, 'dental', 'Dental devices', 'Dental operatory and laboratory equipment.', 14, true),
                (1, 'ophthalmic', 'Ophthalmic devices', 'Eye examination, diagnosis and surgery.', 15, true),
                (1, 'neonatal', 'Neonatal devices', 'Newborn and NICU equipment; a setting-based category that overlaps function-based ones.', 16, true),
                (1, 'home-healthcare', 'Home healthcare devices', 'Used outside the facility. See notes: ownership and location tracking differ.', 17, true),
                (1, 'prosthetic-orthotic', 'Prosthetic and orthotic devices', 'Prostheses, orthoses and the workshop equipment that fabricates them.', 18, true)
            ON CONFLICT (tenant_id, code) DO NOTHING;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
        => throw new NotSupportedException(
            "Migrations are forward-only. Recovery is restore-from-backup, not a down-migration.");
}
