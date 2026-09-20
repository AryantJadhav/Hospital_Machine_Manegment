using HospitalPm.Api.Maintenance;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Equipment;

public sealed record DiagnosisRequest(
    int ChecklistTemplateVersionId,
    Dictionary<string, AnswerInput> Answers,
    DiagnosisOutcome Outcome,
    string? Notes,
    Guid? ClientSubmissionId);

/// <summary>
/// The everyday check of a machine, and the round an engineer makes to do it.
///
/// What is checked comes from a checklist of kind Diagnosis for the machine's type,
/// written by the hospital and versioned like a PM checklist, so the questions can
/// change over the years without rewriting what was recorded under the old ones.
/// </summary>
public static class DiagnosisEndpoints
{
    private const int MaxHistoryRows = 50;

    /// <summary>A whole ward's machines fit; the whole hospital's do not, and should be narrowed.</summary>
    private const int MaxRoundRows = 500;

    public static void MapDiagnosisEndpoints(this IEndpointRouteBuilder app)
    {
        // Every signed-in user. Checking a machine is the everyday job, the
        // one an Employee account exists for.
        var group = app.MapGroup("/api").WithTags("Diagnosis").RequireAuthorization();

        group.MapGet("/equipment/{id:int}/diagnosis/form", FormAsync);
        group.MapPost("/equipment/{id:int}/diagnoses", RecordAsync);
        group.MapGet("/equipment/{id:int}/diagnoses", HistoryAsync);
        group.MapGet("/diagnoses/{id:int}", GetAsync);
        group.MapGet("/diagnosis/rounds", RoundsAsync);
    }

    private static async Task<IResult> FormAsync(
        int id, [FromQuery] int? templateId, HospitalPmDbContext db, CancellationToken ct)
    {
        var machine = await db.Equipment.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new
            {
                e.Id,
                e.AssetTag,
                e.EquipmentTypeId,
                e.Status,
                TypeName = e.EquipmentType!.Name,
                LocationName = e.Location!.Name,
            })
            .SingleOrDefaultAsync(ct);

        if (machine is null)
        {
            return Results.NotFound();
        }

        // Checklists of this machine's type that are ready to use.
        var available = await db.ChecklistTemplates.AsNoTracking()
            .Where(t => t.EquipmentTypeId == machine.EquipmentTypeId
                        && t.Kind == ChecklistKind.Diagnosis
                        && t.IsActive
                        && t.Versions.Any(v => v.Status == ChecklistVersionStatus.Published))
            .OrderBy(t => t.Name)
            .Select(t => new { t.Id, t.Name })
            .ToListAsync(ct);

        if (available.Count == 0)
        {
            return Results.Conflict(new
            {
                error = $"No daily check has been set up for {machine.TypeName} yet. "
                        + "An administrator adds one on the Checklists page, as a Diagnosis checklist for this type.",
            });
        }

        var chosen = available.FirstOrDefault(t => t.Id == templateId) ?? available[0];

        var version = await db.ChecklistTemplateVersions.AsNoTracking()
            .SingleAsync(v => v.ChecklistTemplateId == chosen.Id && v.Status == ChecklistVersionStatus.Published, ct);

        return Results.Ok(new
        {
            machine.Id,
            machine.AssetTag,
            equipmentTypeName = machine.TypeName,
            machine.LocationName,
            status = machine.Status,
            checklistName = chosen.Name,
            checklistTemplateVersionId = version.Id,
            versionNo = version.VersionNo,
            definition = version.Definition,
            available,
        });
    }

    private static async Task<IResult> RecordAsync(
        int id,
        [FromBody] DiagnosisRequest request,
        HospitalPmDbContext db,
        System.Security.Claims.ClaimsPrincipal principal,
        HospitalClock clock,
        CancellationToken ct)
    {
        // A device that lost the reply retries; the retry is the same diagnosis.
        if (request.ClientSubmissionId is { } submissionId)
        {
            var existing = await db.Diagnoses.AsNoTracking()
                .SingleOrDefaultAsync(d => d.ClientSubmissionId == submissionId, ct);

            if (existing is not null)
            {
                return Results.Ok(new
                {
                    diagnosisId = existing.Id,
                    failedCheckCount = existing.FailedCheckCount,
                    outOfRangeCount = existing.OutOfRangeCount,
                    replayed = true,
                });
            }
        }

        var machine = await db.Equipment.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (machine is null)
        {
            return Results.NotFound();
        }

        if (machine.Status is EquipmentStatus.Condemned or EquipmentStatus.Disposed)
        {
            return Results.Conflict(new { error = "A condemned or disposed machine is not diagnosed." });
        }

        if (!Enum.IsDefined(request.Outcome))
        {
            return Results.BadRequest(new { error = "Say whether the machine is working." });
        }

        var found = await db.ChecklistTemplateVersions.AsNoTracking()
            .Where(v => v.Id == request.ChecklistTemplateVersionId)
            .Select(v => new { Version = v, v.Template!.Kind, v.Template.EquipmentTypeId })
            .SingleOrDefaultAsync(ct);

        if (found is null)
        {
            return Results.BadRequest(new { error = "Unknown checklist version." });
        }

        if (found.Kind != ChecklistKind.Diagnosis || found.EquipmentTypeId != machine.EquipmentTypeId)
        {
            return Results.BadRequest(new { error = "That checklist is not a daily check for this kind of machine." });
        }

        if (found.Version.Status == ChecklistVersionStatus.Draft)
        {
            // A draft can still be rewritten, and so could what was recorded against it.
            return Results.BadRequest(new { error = "A diagnosis cannot be recorded against an unpublished draft." });
        }

        var answers = request.Answers?.ToDictionary(
            kv => kv.Key,
            kv => new ChecklistAnswer { Value = kv.Value.Value ?? string.Empty, Note = kv.Value.Note },
            StringComparer.Ordinal) ?? [];

        var validation = ChecklistAnswerValidator.Validate(found.Version.Definition, answers);
        if (!validation.IsValid)
        {
            return Results.BadRequest(new
            {
                error = "The check is not complete.",
                problems = validation.Problems.Select(p => new { item = p.Path, message = p.Message }),
            });
        }

        var diagnosis = new Diagnosis
        {
            TenantId = machine.TenantId,
            EquipmentId = machine.Id,
            ChecklistTemplateVersionId = request.ChecklistTemplateVersionId,
            Answers = validation.Normalised.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            Outcome = request.Outcome,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            LocationId = machine.LocationId,
            PerformedByUserId = EquipmentMoveEndpoints.UserId(principal),
            PerformedAtUtc = clock.UtcNow(),
            ClientSubmissionId = request.ClientSubmissionId,
        };

        db.Diagnoses.Add(diagnosis);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            diagnosisId = diagnosis.Id,
            failedCheckCount = diagnosis.FailedCheckCount,
            outOfRangeCount = diagnosis.OutOfRangeCount,
            replayed = false,
        });
    }

    private static async Task<IResult> HistoryAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        if (!await db.Equipment.AnyAsync(e => e.Id == id, ct))
        {
            return Results.NotFound();
        }

        var rows = await db.Diagnoses.AsNoTracking()
            .Where(d => d.EquipmentId == id)
            .OrderByDescending(d => d.PerformedAtUtc).ThenByDescending(d => d.Id)
            .Take(MaxHistoryRows)
            .ToListAsync(ct);

        var locationIds = rows.Select(d => d.LocationId).Distinct().ToList();
        var userIds = rows.Select(d => d.PerformedByUserId).Distinct().ToList();
        var locations = await db.Locations.AsNoTracking()
            .Where(l => locationIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var users = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        return Results.Ok(rows.Select(d => new
        {
            d.Id,
            d.PerformedAtUtc,
            d.Outcome,
            d.Notes,
            failedChecks = d.FailedCheckCount,
            outOfRange = d.OutOfRangeCount,
            location = locations.GetValueOrDefault(d.LocationId),
            performedBy = users.GetValueOrDefault(d.PerformedByUserId),
        }));
    }

    /// <summary>One recorded diagnosis, shown against the version it was filled under.</summary>
    private static async Task<IResult> GetAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        var d = await db.Diagnoses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (d is null)
        {
            return Results.NotFound();
        }

        var version = await db.ChecklistTemplateVersions.AsNoTracking()
            .SingleAsync(v => v.Id == d.ChecklistTemplateVersionId, ct);

        var machine = await db.Equipment.AsNoTracking()
            .Where(e => e.Id == d.EquipmentId)
            .Select(e => new { e.AssetTag, TypeName = e.EquipmentType!.Name })
            .SingleAsync(ct);

        var checklistName = await db.ChecklistTemplates.AsNoTracking()
            .Where(t => t.Id == version.ChecklistTemplateId).Select(t => t.Name).FirstOrDefaultAsync(ct);
        var location = await db.Locations.AsNoTracking()
            .Where(l => l.Id == d.LocationId).Select(l => l.Name).FirstOrDefaultAsync(ct);
        var performedBy = await db.Users.AsNoTracking()
            .Where(u => u.Id == d.PerformedByUserId).Select(u => u.FullName).FirstOrDefaultAsync(ct);

        return Results.Ok(new
        {
            d.Id,
            d.EquipmentId,
            machine.AssetTag,
            equipmentTypeName = machine.TypeName,
            checklistName,
            location,
            performedBy,
            d.PerformedAtUtc,
            d.Outcome,
            d.Notes,
            versionNo = version.VersionNo,
            definition = version.Definition,
            answers = d.Answers,
        });
    }

    /// <summary>
    /// The round: every machine in a place (and everything inside it), with whether
    /// it has been checked today and how that went.
    /// </summary>
    private static async Task<IResult> RoundsAsync(
        [FromQuery] int? locationId, HospitalPmDbContext db, HospitalClock clock, CancellationToken ct)
    {
        var today = clock.Today();

        // Today at the hospital, as the two instants that bound it.
        var dayStart = DateTime.SpecifyKind(today.ToDateTime(TimeOnly.MinValue) - clock.Offset, DateTimeKind.Utc);
        var dayEnd = dayStart.AddDays(1);

        var machines = db.Equipment.AsNoTracking()
            .Where(e => e.Status == EquipmentStatus.InService || e.Status == EquipmentStatus.UnderRepair);

        if (locationId is not null)
        {
            var path = await db.Locations.AsNoTracking()
                .Where(l => l.Id == locationId)
                .Select(l => l.Path)
                .SingleOrDefaultAsync(ct);

            if (path is null)
            {
                return Results.NotFound();
            }

            machines = machines.Where(e => e.Location!.Path.StartsWith(path));
        }

        var total = await machines.CountAsync(ct);

        var rows = await machines
            .OrderBy(e => e.Location!.Path).ThenBy(e => e.AssetTag)
            .Take(MaxRoundRows)
            .Select(e => new
            {
                e.Id,
                e.AssetTag,
                typeName = e.EquipmentType!.Name,
                location = e.Location!.Name,
                e.Status,
                hasChecklist = db.ChecklistTemplates.Any(t => t.EquipmentTypeId == e.EquipmentTypeId
                    && t.Kind == ChecklistKind.Diagnosis && t.IsActive
                    && t.Versions.Any(v => v.Status == ChecklistVersionStatus.Published)),
                checkedToday = db.Diagnoses
                    .Where(d => d.EquipmentId == e.Id && d.PerformedAtUtc >= dayStart && d.PerformedAtUtc < dayEnd)
                    .OrderByDescending(d => d.PerformedAtUtc)
                    .Select(d => new
                    {
                        d.Id,
                        d.Outcome,
                        d.PerformedAtUtc,
                        by = db.Users.Where(u => u.Id == d.PerformedByUserId).Select(u => u.FullName).FirstOrDefault(),
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return Results.Ok(new { date = today.ToString("yyyy-MM-dd"), total, shown = rows.Count, items = rows });
    }
}
