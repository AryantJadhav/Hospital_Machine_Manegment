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
            .RequireAuthorization(p => p.RequireRole(Roles.Admin, Roles.BiomedicalHead, Roles.SeniorEngineer));

        owner.MapPost("/schedules", CreateScheduleAsync);
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
