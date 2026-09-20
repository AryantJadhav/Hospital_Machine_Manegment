using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Maintenance;

public sealed record ScheduleRequest(
    int EquipmentId,
    int ChecklistTemplateId,
    PmFrequency Frequency,
    int IntervalDays,
    DateOnly AnchorDate,
    int GraceDays);

/// <summary>
/// Schedules one checklist across a whole equipment type at once.
///
/// The equipment type is NOT a field here: a checklist template belongs to
/// exactly one type, so taking both would invite a mismatch that can only be
/// rejected. The template decides what gets scheduled.
///
/// <see cref="LocationId"/> narrows it to one part of the hospital and
/// everything beneath it — "every defibrillator in the Cardiac Wing" — because
/// a large hospital commissions a ward at a time rather than a fleet at once.
/// </summary>
public sealed record BulkScheduleRequest(
    int ChecklistTemplateId,
    PmFrequency Frequency,
    int IntervalDays,
    DateOnly AnchorDate,
    int GraceDays,
    int? LocationId,
    bool IncludeInStore);

public sealed record BulkScheduleResponse(
    int Created,
    int AlreadyScheduled,
    int SkippedRetired,
    int SkippedNotYetInService,
    int Considered);

public sealed record ScheduleResponse(
    int Id,
    int EquipmentId,
    string AssetTag,
    int ChecklistTemplateId,
    string ChecklistName,
    PmFrequency Frequency,
    int IntervalDays,
    DateOnly AnchorDate,
    int GraceDays,
    bool IsActive,
    DateOnly? NextDueDate);

public sealed record TaskResponse(
    int Id,
    int PmScheduleId,
    int EquipmentId,
    string AssetTag,
    string EquipmentTypeName,
    string LocationName,
    string ChecklistName,
    DateOnly DueDate,
    PmTaskStatus Status,
    int DaysLate);

public static class PmEndpoints
{
    private const int MaxPageSize = 200;

    public static void MapPmEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pm").WithTags("Preventive maintenance").RequireAuthorization();

        // Every role reads the work list: it is what a technician works from.
        group.MapGet("/tasks", TasksAsync);
        group.MapGet("/schedules", SchedulesAsync);
        group.MapGet("/summary", SummaryAsync);

        var owner = group.MapGroup(string.Empty)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        owner.MapPost("/schedules", CreateScheduleAsync);
        owner.MapPost("/schedules/bulk", CreateSchedulesBulkAsync);
        owner.MapPost("/generate", GenerateAsync);
    }

    /// <summary>
    /// The work list. Defaults to everything open, soonest first, because
    /// that is what a technician starting a shift wants.
    /// </summary>
    private static async Task<IResult> TasksAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        [FromQuery] PmTaskStatus? status,
        [FromQuery] int? locationId,
        [FromQuery] int? equipmentId,
        [FromQuery] string? q,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var today = clock.Today();

        var query = db.PmTasks.AsNoTracking();

        query = status is not null
            ? query.Where(t => t.Status == status)
            : query.Where(t => t.Status == PmTaskStatus.Scheduled
                            || t.Status == PmTaskStatus.Due
                            || t.Status == PmTaskStatus.Overdue);

        if (equipmentId is not null)
        {
            query = query.Where(t => t.EquipmentId == equipmentId);
        }

        // Whatever a technician can read off the machine in front of them or the
        // row they are looking for. The work list is hundreds of rows long at a
        // real hospital, and there was no way to find one machine in it but to
        // page through, 25 at a time.
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLowerInvariant();
            query = query.Where(t =>
                t.Equipment!.AssetTag.ToLower().Contains(term) ||
                (t.Equipment!.SerialNumber != null && t.Equipment!.SerialNumber.ToLower().Contains(term)) ||
                (t.Equipment!.Manufacturer != null && t.Equipment!.Manufacturer.ToLower().Contains(term)) ||
                (t.Equipment!.Model != null && t.Equipment!.Model.ToLower().Contains(term)) ||
                t.Equipment!.EquipmentType!.Name.ToLower().Contains(term) ||
                t.Equipment!.Location!.Name.ToLower().Contains(term) ||
                t.Schedule!.ChecklistTemplate!.Name.ToLower().Contains(term));
        }

        if (locationId is not null)
        {
            // Subtree, so asking for a site returns work in every department
            // under it.
            var prefix = await db.Locations
                .Where(l => l.Id == locationId)
                .Select(l => l.Path)
                .SingleOrDefaultAsync(ct);

            if (prefix is null)
            {
                return Results.NotFound(new { error = "Unknown location." });
            }

            query = query.Where(t => t.Equipment!.Location!.Path.StartsWith(prefix));
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(t => t.DueDate)
            .ThenBy(t => t.Equipment!.AssetTag)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new TaskResponse(
                t.Id,
                t.PmScheduleId,
                t.EquipmentId,
                t.Equipment!.AssetTag,
                t.Equipment!.EquipmentType!.Name,
                t.Equipment!.Location!.Name,
                t.Schedule!.ChecklistTemplate!.Name,
                t.DueDate,
                t.Status,
                // Computed here rather than stored: a stored "days late" is
                // wrong the moment the clock ticks past midnight.
                t.DueDate < today ? today.DayNumber - t.DueDate.DayNumber : 0))
            .ToListAsync(ct);

        return Results.Ok(new { items, total, page, pageSize });
    }

    private static async Task<IResult> SchedulesAsync(
        HospitalPmDbContext db, [FromQuery] int? equipmentId, CancellationToken ct)
    {
        var query = db.PmSchedules.AsNoTracking();

        if (equipmentId is not null)
        {
            query = query.Where(s => s.EquipmentId == equipmentId);
        }

        var items = await query
            .OrderBy(s => s.Equipment!.AssetTag)
            .Select(s => new ScheduleResponse(
                s.Id,
                s.EquipmentId,
                s.Equipment!.AssetTag,
                s.ChecklistTemplateId,
                s.ChecklistTemplate!.Name,
                s.Frequency,
                s.IntervalDays,
                s.AnchorDate,
                s.GraceDays,
                s.IsActive,
                s.Tasks
                    .Where(t => t.Status != PmTaskStatus.Completed && t.Status != PmTaskStatus.Skipped)
                    .OrderBy(t => t.DueDate)
                    .Select(t => (DateOnly?)t.DueDate)
                    .FirstOrDefault()))
            .ToListAsync(ct);

        return Results.Ok(items);
    }

    /// <summary>Counts for the dashboard.</summary>
    private static async Task<IResult> SummaryAsync(
        HospitalPmDbContext db, HospitalClock clock, CancellationToken ct)
    {
        var today = clock.Today();
        var weekEnd = today.AddDays(7);

        return Results.Ok(new
        {
            overdue = await db.PmTasks.CountAsync(t => t.Status == PmTaskStatus.Overdue, ct),
            dueToday = await db.PmTasks.CountAsync(t => t.Status == PmTaskStatus.Due && t.DueDate == today, ct),
            dueThisWeek = await db.PmTasks.CountAsync(
                t => (t.Status == PmTaskStatus.Due || t.Status == PmTaskStatus.Scheduled)
                     && t.DueDate >= today && t.DueDate <= weekEnd, ct),
            completedThisMonth = await db.PmTasks.CountAsync(
                t => t.Status == PmTaskStatus.Completed
                     && t.CompletedAtUtc != null
                     && t.CompletedAtUtc!.Value.Year == today.Year
                     && t.CompletedAtUtc!.Value.Month == today.Month, ct),
            activeSchedules = await db.PmSchedules.CountAsync(s => s.IsActive, ct),
        });
    }

    /// <summary>
    /// Puts one checklist on every machine of its type, in one call.
    ///
    /// The reason this exists: creating schedules one at a time meant a
    /// hospital with 2,000 assets faced 2,000 requests to set up preventive
    /// maintenance. That is not a slow path, it is a path nobody walks — and
    /// it is why the product could hold an equipment register and still never
    /// schedule a PM.
    ///
    /// Idempotent, so running it again after commissioning another ward adds
    /// only what is new. Machines already carrying this checklist are counted
    /// and left alone rather than rejected, because "some of these are already
    /// done" is the normal case, not an error.
    /// </summary>
    private static async Task<IResult> CreateSchedulesBulkAsync(
        [FromBody] BulkScheduleRequest request,
        HospitalPmDbContext db,
        PmScheduleGenerator generator,
        CancellationToken ct)
    {
        var template = await db.ChecklistTemplates
            .Where(t => t.Id == request.ChecklistTemplateId)
            .Select(t => new
            {
                t.Id,
                t.EquipmentTypeId,
                Published = t.Versions.Any(v => v.Status == ChecklistVersionStatus.Published),
            })
            .SingleOrDefaultAsync(ct);

        if (template is null)
        {
            return Results.BadRequest(new { error = "Unknown checklist." });
        }

        // A draft can still be edited, and a completion has to be tied to the
        // exact version it was filled under. Scheduling against a checklist
        // that has never been published would promise a technician work that
        // has no questions in it.
        if (!template.Published)
        {
            return Results.BadRequest(new
            {
                error = "That checklist has never been published, so it cannot be scheduled yet.",
            });
        }

        if (request.Frequency == PmFrequency.Custom && request.IntervalDays < 1)
        {
            return Results.BadRequest(new { error = "A custom frequency needs an interval in days." });
        }

        var candidates = db.Equipment.Where(e => e.EquipmentTypeId == template.EquipmentTypeId);

        if (request.LocationId is int locationId)
        {
            var prefix = await db.Locations
                .Where(l => l.Id == locationId)
                .Select(l => l.Path)
                .SingleOrDefaultAsync(ct);

            if (prefix is null)
            {
                return Results.NotFound(new { error = "Unknown location." });
            }

            candidates = candidates.Where(e => e.Location!.Path.StartsWith(prefix));
        }

        var equipment = await candidates
            .Select(e => new { e.Id, e.Status })
            .ToListAsync(ct);

        var alreadyScheduled = await db.PmSchedules
            .Where(s => s.ChecklistTemplateId == template.Id)
            .Select(s => s.EquipmentId)
            .ToListAsync(ct);

        var have = alreadyScheduled.ToHashSet();

        var created = 0;
        var skippedRetired = 0;
        var skippedNotYetInService = 0;
        var existing = 0;

        foreach (var machine in equipment)
        {
            if (have.Contains(machine.Id))
            {
                existing++;
                continue;
            }

            // Condemned and disposed machines stay on the register so their
            // certificates remain readable, and are never maintained again.
            if (machine.Status is EquipmentStatus.Condemned or EquipmentStatus.Disposed)
            {
                skippedRetired++;
                continue;
            }

            // In store is not yet in use. Scheduling it produces PM tasks for
            // a machine sitting in a cupboard, which reads as overdue work
            // that nobody can sensibly do — so it is opt-in rather than a
            // default that quietly fills a technician's list.
            if (machine.Status == EquipmentStatus.InStore && !request.IncludeInStore)
            {
                skippedNotYetInService++;
                continue;
            }

            db.PmSchedules.Add(new PmSchedule
            {
                EquipmentId = machine.Id,
                ChecklistTemplateId = template.Id,
                Frequency = request.Frequency,
                IntervalDays = request.Frequency == PmFrequency.Custom ? request.IntervalDays : 0,
                AnchorDate = request.AnchorDate,
                GraceDays = request.GraceDays,
            });

            created++;
        }

        if (created > 0)
        {
            await db.SaveChangesAsync(ct);

            // Generated immediately, as the single-schedule path does, so the
            // work appears rather than waiting for the nightly job.
            await generator.RunAsync(ct);
        }

        return Results.Ok(new BulkScheduleResponse(
            Created: created,
            AlreadyScheduled: existing,
            SkippedRetired: skippedRetired,
            SkippedNotYetInService: skippedNotYetInService,
            Considered: equipment.Count));
    }

    private static async Task<IResult> CreateScheduleAsync(
        [FromBody] ScheduleRequest request,
        HospitalPmDbContext db,
        PmScheduleGenerator generator,
        CancellationToken ct)
    {
        if (!await db.Equipment.AnyAsync(e => e.Id == request.EquipmentId, ct))
        {
            return Results.BadRequest(new { error = "Unknown equipment." });
        }

        var template = await db.ChecklistTemplates
            .Where(t => t.Id == request.ChecklistTemplateId)
            .Select(t => new { t.Id, t.EquipmentTypeId })
            .SingleOrDefaultAsync(ct);

        if (template is null)
        {
            return Results.BadRequest(new { error = "Unknown checklist." });
        }

        var equipmentTypeId = await db.Equipment
            .Where(e => e.Id == request.EquipmentId)
            .Select(e => e.EquipmentTypeId)
            .SingleAsync(ct);

        // A ventilator checklist on an ultrasound is not a validation
        // technicality; it is a technician being asked questions that do not
        // apply to the machine in front of them.
        if (template.EquipmentTypeId != equipmentTypeId)
        {
            return Results.BadRequest(new
            {
                error = "That checklist belongs to a different equipment type.",
            });
        }

        if (request.Frequency == PmFrequency.Custom && request.IntervalDays < 1)
        {
            return Results.BadRequest(new { error = "A custom frequency needs an interval in days." });
        }

        if (await db.PmSchedules.AnyAsync(
                s => s.EquipmentId == request.EquipmentId
                     && s.ChecklistTemplateId == request.ChecklistTemplateId, ct))
        {
            return Results.Conflict(new
            {
                error = "This machine already has a schedule for that checklist.",
            });
        }

        var schedule = new PmSchedule
        {
            EquipmentId = request.EquipmentId,
            ChecklistTemplateId = request.ChecklistTemplateId,
            Frequency = request.Frequency,
            IntervalDays = request.Frequency == PmFrequency.Custom ? request.IntervalDays : 0,
            AnchorDate = request.AnchorDate,
            GraceDays = request.GraceDays,
        };

        db.PmSchedules.Add(schedule);
        await db.SaveChangesAsync(ct);

        // Generated immediately so the schedule is not invisible until the
        // nightly job runs.
        await generator.RunAsync(ct);

        return Results.Created($"/api/pm/schedules/{schedule.Id}", new { schedule.Id });
    }

    /// <summary>
    /// Runs the generator on demand. The nightly job does this anyway; this
    /// exists so a biomedical head who just loaded 2,000 assets does not have
    /// to wait until tomorrow to see the work.
    /// </summary>
    private static async Task<IResult> GenerateAsync(PmScheduleGenerator generator, CancellationToken ct)
    {
        var result = await generator.RunAsync(ct);

        return Results.Ok(new
        {
            created = result.Created,
            transitioned = result.Transitioned,
            schedulesConsidered = result.SchedulesConsidered,
        });
    }
}
