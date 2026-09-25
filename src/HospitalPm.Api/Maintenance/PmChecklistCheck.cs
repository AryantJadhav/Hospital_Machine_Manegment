using HospitalPm.Domain.Checklists;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Maintenance;

/// <summary>
/// A PM does not need a checklist. Most of them are only scheduled and then recorded as done,
/// with who did it, when and, if there is one, the report. So where a checklist is offered it is
/// optional, and this checks one only when it is given.
/// </summary>
public static class PmChecklistCheck
{
    /// <summary>Null when there is no checklist, or when the one given fits the machine's type.</summary>
    public static async Task<IResult?> ValidateAsync(
        int? checklistTemplateId, int equipmentTypeId, HospitalPmDbContext db, CancellationToken ct)
    {
        if (checklistTemplateId is null)
        {
            return null;
        }

        var template = await db.ChecklistTemplates.AsNoTracking()
            .Where(t => t.Id == checklistTemplateId && t.Kind == ChecklistKind.Pm)
            .Select(t => new { t.EquipmentTypeId })
            .SingleOrDefaultAsync(ct);

        if (template is null)
        {
            return Results.BadRequest(new { error = "Unknown checklist." });
        }

        // A ventilator checklist on an ultrasound is a technician being asked questions that
        // do not apply to the machine in front of them.
        if (template.EquipmentTypeId != equipmentTypeId)
        {
            return Results.BadRequest(new { error = "That checklist belongs to a different equipment type." });
        }

        return null;
    }
}
