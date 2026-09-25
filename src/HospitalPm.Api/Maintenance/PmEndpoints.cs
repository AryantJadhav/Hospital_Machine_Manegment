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
    // Optional: a PM is scheduled and recorded as done, and needs no checklist.
    int? ChecklistTemplateId,
    PmFrequency Frequency,
    int IntervalDays,
    DateOnly AnchorDate,
    int GraceDays,
    // Who does the PM. Left out, the hospital's own team. The vendor only on a machine that
    // has a maintenance contract (AMC or CMC).
    PmPerformedBy PerformedBy = PmPerformedBy.InHouse);

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
/// <summary>
/// Changing a schedule that already exists: how often, the date the next PM falls
/// due, the grace, and whether it is running. The checklist is not here. A different
/// checklist is a different schedule.
/// </summary>
public sealed record UpdateScheduleRequest(
    PmFrequency Frequency,
    int IntervalDays,
    DateOnly NextDueDate,
    int GraceDays,
    bool IsActive);

/// <summary>
/// PM dates picked one by one for a machine that already exists: a date at a time, or several,
/// and more later. They are added to that machine's hand-picked schedule for the checklist,
/// which is made the first time.
/// </summary>
public sealed record ManualDatesRequest(
    int EquipmentId,
    int? ChecklistTemplateId,
    IReadOnlyList<DateOnly> Dates,
    int GraceDays = 7,
    PmPerformedBy PerformedBy = PmPerformedBy.InHouse);

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
    int? ChecklistTemplateId,
    string? ChecklistName,
    PmFrequency Frequency,
    int IntervalDays,
    DateOnly AnchorDate,
    int GraceDays,
    bool IsActive,
    // The earliest PM still open, which may be long overdue.
    DateOnly? NextDueDate,
    // The next date the schedule falls on from today, whatever has or has not been done.
    // This is the one to start from when the pattern is changed: starting from an
    // overdue date would count new PMs from the past.
    DateOnly? NextUpcomingDate = null);

/// <summary>One PM that somebody needs to know about.</summary>
public sealed record ReminderItem(
    int TaskId,
    int EquipmentId,
    string AssetTag,
    string EquipmentTypeName,
    string LocationName,
    string ChecklistName,
    DateOnly DueDate,
    PmTaskStatus Status,
    // Days from today to the due date. Negative once it has passed.
    int DaysFromToday);

/// <summary>
/// What the bell in the top bar shows.
///
/// The counts are of everything that qualifies; the lists are the first few of each, so a
/// hospital with 300 overdue PMs is not sent 300 rows every time somebody looks.
/// </summary>
public sealed record RemindersResponse(
    int LeadDays,
    int OverdueCount,
    int UpcomingCount,
    IReadOnlyList<ReminderItem> Overdue,
    IReadOnlyList<ReminderItem> Upcoming);

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
    int DaysLate,
    PmPerformedBy PerformedBy,
    // False for a PM that is only scheduled and recorded as done.
    bool HasChecklist);

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
        group.MapGet("/preview", Preview);
        group.MapGet("/reminders", RemindersAsync);

        var owner = group.MapGroup(string.Empty)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        owner.MapPost("/schedules", CreateScheduleAsync);
        owner.MapPost("/schedules/bulk", CreateSchedulesBulkAsync);
        owner.MapPut("/schedules/{id:int}", UpdateScheduleAsync);
        owner.MapPost("/schedules/dates", AddDatesAsync);
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

            // Decided on the machine first, then matched to its tasks. Filtering
            // the tasks directly made the database join every open task to its
            // machine, type and place before it could test a single word; with a
            // real register (15,000 machines) that was the slowest thing in the
            // product. The machines are far fewer than the tasks.
            var machines = db.Equipment.Where(e =>
                e.AssetTag.ToLower().Contains(term) ||
                (e.SerialNumber != null && e.SerialNumber.ToLower().Contains(term)) ||
                (e.Manufacturer != null && e.Manufacturer.ToLower().Contains(term)) ||
                (e.Model != null && e.Model.ToLower().Contains(term)) ||
                e.EquipmentType!.Name.ToLower().Contains(term) ||
                e.Location!.Name.ToLower().Contains(term))
                .Select(e => e.Id);

            var templates = db.ChecklistTemplates
                .Where(c => c.Name.ToLower().Contains(term))
                .Select(c => c.Id);

            query = query.Where(t =>
                machines.Contains(t.EquipmentId) ||
                (t.Schedule!.ChecklistTemplateId != null && templates.Contains(t.Schedule!.ChecklistTemplateId.Value)));
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
                t.Schedule!.ChecklistTemplate!.Name ?? "PM",
                t.DueDate,
                t.Status,
                // Computed here rather than stored: a stored "days late" is
                // wrong the moment the clock ticks past midnight.
                t.DueDate < today ? today.DayNumber - t.DueDate.DayNumber : 0,
                t.Schedule!.PerformedBy,
                t.Schedule!.ChecklistTemplateId != null))
            .ToListAsync(ct);

        return Results.Ok(new { items, total, page, pageSize });
    }

    private static async Task<IResult> SchedulesAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        [FromQuery] int? equipmentId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.PmSchedules.AsNoTracking();

        if (equipmentId is not null)
        {
            query = query.Where(s => s.EquipmentId == equipmentId);
        }

        var total = await query.CountAsync(ct);
        var today = clock.Today();

        var rows = await query
            .OrderBy(s => s.Equipment!.AssetTag)
            .ThenBy(s => s.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
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

        var items = rows.Select(r => r.Frequency == PmFrequency.Manual
            // Hand-picked dates have no pattern to say where it falls next.
            ? r
            : r with
            {
                // First occurrence on or after today.
                NextUpcomingDate = PmDueDates.NextAfter(r.AnchorDate, r.Frequency, r.IntervalDays, today.AddDays(-1)),
            });

        return Results.Ok(new { items, total, page, pageSize });
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
            .Where(t => t.Id == request.ChecklistTemplateId && t.Kind == ChecklistKind.Pm)
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

        if (request.Frequency == PmFrequency.Manual)
        {
            return Results.BadRequest(new { error = "Dates that are picked one by one are added to a machine, not scheduled here." });
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

    /// <summary>
    /// The PMs everyone should know about: those already overdue, and those that fall due
    /// within the reminder lead time (seven days by default).
    ///
    /// Open to every role, and the same for all of them. PMs are not assigned to a person:
    /// the work list is the department's, so the reminder is too. Worked out when asked,
    /// from the tasks themselves, so nothing has to be sent, stored or marked as read, and
    /// it needs no mail server, no internet and no new background job.
    /// </summary>
    private static async Task<IResult> RemindersAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        Microsoft.Extensions.Options.IOptions<ScheduleOptions> options,
        CancellationToken ct)
    {
        const int MaxListed = 25;

        var today = clock.Today();
        var leadDays = Math.Max(0, options.Value.ReminderLeadDays);
        var until = today.AddDays(leadDays);

        var overdue = db.PmTasks.AsNoTracking().Where(t => t.Status == PmTaskStatus.Overdue);

        // Due, or still to come, but not past its grace. Completed and skipped are done.
        var upcoming = db.PmTasks.AsNoTracking()
            .Where(t => (t.Status == PmTaskStatus.Due || t.Status == PmTaskStatus.Scheduled)
                        && t.DueDate <= until);

        var overdueCount = await overdue.CountAsync(ct);
        var upcomingCount = await upcoming.CountAsync(ct);

        // The most overdue first, then the soonest, which is the order somebody would act in.
        // Ordered and cut before they are shaped: EF cannot order by a field of a record
        // that has just been built in the same query.
        var overdueRows = await Project(
            overdue.OrderBy(t => t.DueDate).ThenBy(t => t.Id).Take(MaxListed)).ToListAsync(ct);
        var upcomingRows = await Project(
            upcoming.OrderBy(t => t.DueDate).ThenBy(t => t.Id).Take(MaxListed)).ToListAsync(ct);

        ReminderItem Item(ReminderRow r) => new(
            r.TaskId, r.EquipmentId, r.AssetTag, r.EquipmentTypeName, r.LocationName, r.ChecklistName,
            r.DueDate, r.Status, r.DueDate.DayNumber - today.DayNumber);

        return Results.Ok(new RemindersResponse(
            leadDays, overdueCount, upcomingCount,
            overdueRows.Select(Item).ToList(), upcomingRows.Select(Item).ToList()));
    }

    private sealed record ReminderRow(
        int TaskId, int EquipmentId, string AssetTag, string EquipmentTypeName, string LocationName,
        string ChecklistName, DateOnly DueDate, PmTaskStatus Status);

    private static IQueryable<ReminderRow> Project(IQueryable<PmTask> tasks) => tasks.Select(t => new ReminderRow(
        t.Id,
        t.EquipmentId,
        t.Equipment!.AssetTag,
        t.Equipment!.EquipmentType!.Name,
        t.Equipment!.Location!.Name,
        t.Schedule!.ChecklistTemplate!.Name ?? "PM",
        t.DueDate,
        t.Status));

    /// <summary>
    /// The dates a schedule would fall due on in its first year, worked out by the same
    /// code that generates the real ones. The add-a-machine form shows them before
    /// anything is saved, so the person choosing "quarterly" sees the four dates that
    /// means rather than trusting a word.
    /// </summary>
    private static IResult Preview([FromQuery] PmFrequency frequency, [FromQuery] DateOnly anchorDate)
    {
        var months = frequency.Months();
        if (months is null)
        {
            return Results.BadRequest(new
            {
                error = "Choose monthly, every 2 months, quarterly, half-yearly or yearly.",
            });
        }

        // The first year: from the anchor up to the day before it comes round again, so
        // monthly gives twelve dates and yearly gives one.
        var dates = PmDueDates.Between(
            anchorDate, frequency, 0, from: anchorDate, horizon: anchorDate.AddYears(1).AddDays(-1));

        return Results.Ok(new { timesPerYear = 12 / months.Value, dates });
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

        var equipmentTypeId = await db.Equipment
            .Where(e => e.Id == request.EquipmentId)
            .Select(e => e.EquipmentTypeId)
            .SingleAsync(ct);

        var checklistError = await PmChecklistCheck.ValidateAsync(request.ChecklistTemplateId, equipmentTypeId, db, ct);
        if (checklistError is not null)
        {
            return checklistError;
        }

        if (request.Frequency == PmFrequency.Manual)
        {
            return Results.BadRequest(new { error = "Dates that are picked one by one are added to a machine, not scheduled here." });
        }

        if (request.Frequency == PmFrequency.Custom && request.IntervalDays < 1)
        {
            return Results.BadRequest(new { error = "A custom frequency needs an interval in days." });
        }

        if (!Enum.IsDefined(request.PerformedBy))
        {
            return Results.BadRequest(new { error = "Say whether the hospital's team or the vendor does this PM." });
        }

        if (request.PerformedBy == PmPerformedBy.Vendor
            && !await db.Equipment.AnyAsync(
                e => e.Id == request.EquipmentId && e.MaintenanceContractType != null, ct))
        {
            return Results.BadRequest(new
            {
                error = "The vendor can only do the PM on a machine that has a maintenance contract (AMC or CMC).",
            });
        }

        // The same checklist twice, or two schedules with no checklist for the same doer, would
        // generate duplicate work.
        if (await db.PmSchedules.AnyAsync(
                s => s.EquipmentId == request.EquipmentId
                     && s.ChecklistTemplateId == request.ChecklistTemplateId
                     && (request.ChecklistTemplateId != null || s.PerformedBy == request.PerformedBy), ct))
        {
            return Results.Conflict(new
            {
                error = request.ChecklistTemplateId is null
                    ? "This machine already has a PM schedule of that kind."
                    : "This machine already has a schedule for that checklist.",
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
            PerformedBy = request.PerformedBy,
        };

        db.PmSchedules.Add(schedule);
        await db.SaveChangesAsync(ct);

        // Generated immediately so the schedule is not invisible until the
        // nightly job runs.
        await generator.RunAsync(ct);

        return Results.Created($"/api/pm/schedules/{schedule.Id}", new { schedule.Id });
    }

    private static async Task<IResult> AddDatesAsync(
        [FromBody] ManualDatesRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        CancellationToken ct)
    {
        var (dates, dateError) = ManualPmDates.Clean(request.Dates);
        if (dateError is not null)
        {
            return Results.BadRequest(new { error = dateError });
        }

        if (request.GraceDays is < 0 or > 90)
        {
            return Results.BadRequest(new { error = "Grace days must be between 0 and 90." });
        }

        if (!Enum.IsDefined(request.PerformedBy))
        {
            return Results.BadRequest(new { error = "Say whether the hospital's team or the vendor does this PM." });
        }

        var machine = await db.Equipment.AsNoTracking()
            .Where(e => e.Id == request.EquipmentId)
            .Select(e => new { e.Status, e.EquipmentTypeId, HasContract = e.MaintenanceContractType != null })
            .SingleOrDefaultAsync(ct);

        if (machine is null)
        {
            return Results.BadRequest(new { error = "Unknown equipment." });
        }

        if (machine.Status is EquipmentStatus.Condemned or EquipmentStatus.Disposed)
        {
            return Results.Conflict(new { error = "A condemned or disposed machine is never maintained again." });
        }

        var checklistError = await PmChecklistCheck.ValidateAsync(
            request.ChecklistTemplateId, machine.EquipmentTypeId, db, ct);
        if (checklistError is not null)
        {
            return checklistError;
        }

        if (request.PerformedBy == PmPerformedBy.Vendor && !machine.HasContract)
        {
            return Results.BadRequest(new
            {
                error = "The vendor can only do the PM on a machine that has a maintenance contract (AMC or CMC).",
            });
        }

        // With no checklist, a machine has one such schedule for each of who does it, so the team's
        // dates and the vendor's are kept apart.
        var schedule = await db.PmSchedules.SingleOrDefaultAsync(
            s => s.EquipmentId == request.EquipmentId
                 && s.ChecklistTemplateId == request.ChecklistTemplateId
                 && (request.ChecklistTemplateId != null || s.PerformedBy == request.PerformedBy), ct);

        if (schedule is not null && schedule.Frequency != PmFrequency.Manual)
        {
            return Results.Conflict(new
            {
                error = "This machine already has a repeating schedule for that. Its dates follow that pattern.",
            });
        }

        if (schedule is not null && schedule.PerformedBy != request.PerformedBy)
        {
            return Results.Conflict(new
            {
                error = "This machine's dates for that checklist are already set to be done by "
                        + (schedule.PerformedBy == PmPerformedBy.Vendor ? "the vendor" : "the hospital's own team") + ".",
            });
        }

        if (schedule is null)
        {
            schedule = new PmSchedule
            {
                EquipmentId = request.EquipmentId,
                ChecklistTemplateId = request.ChecklistTemplateId,
                Frequency = PmFrequency.Manual,
                IntervalDays = 0,
                AnchorDate = dates![0],
                GraceDays = request.GraceDays,
                PerformedBy = request.PerformedBy,
            };
            db.PmSchedules.Add(schedule);
        }
        else
        {
            schedule.IsActive = true;
        }

        // A date that is already a PM on this schedule is left as it is, not made twice.
        var have = schedule.Id == 0
            ? new HashSet<DateOnly>()
            : (await db.PmTasks.Where(t => t.PmScheduleId == schedule.Id).Select(t => t.DueDate).ToListAsync(ct)).ToHashSet();

        var toAdd = dates!.Where(d => !have.Contains(d)).ToList();
        db.PmTasks.AddRange(ManualPmDates.Tasks(schedule, equipment: null, toAdd, clock.Today()));

        await db.SaveChangesAsync(ct);

        return Results.Ok(new { scheduleId = schedule.Id, added = toAdd.Count, alreadyThere = dates!.Count - toAdd.Count });
    }

    /// <summary>
    /// Changes how a machine's PM runs.
    ///
    /// The date given becomes the new anchor: every date after it is counted from there.
    /// When the pattern changes (how often, or the anchor) the PMs that were generated
    /// ahead and not yet due are removed and made again under the new pattern; without
    /// that a machine moved from quarterly to monthly would carry both sets of dates.
    ///
    /// Nothing that has happened is touched. Completed and skipped PMs are records and
    /// the database will not delete them, and PMs already due or overdue are real work
    /// somebody still owes, so they stay on the list until they are done or skipped with
    /// a reason. Stopping a schedule removes only what is ahead.
    /// </summary>
    private static async Task<IResult> UpdateScheduleAsync(
        int id,
        [FromBody] UpdateScheduleRequest request,
        HospitalPmDbContext db,
        PmScheduleGenerator generator,
        CancellationToken ct)
    {
        var schedule = await db.PmSchedules.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (schedule is null)
        {
            return Results.NotFound();
        }

        // A schedule of hand-picked dates has no pattern to change, and a repeating one cannot
        // become one: its dates are added, and skipped when they will not happen.
        if (schedule.Frequency == PmFrequency.Manual || request.Frequency == PmFrequency.Manual)
        {
            return Results.BadRequest(new { error = "The dates of this schedule are picked one by one, so there is no pattern to change." });
        }

        if (request.Frequency == PmFrequency.Custom)
        {
            if (request.IntervalDays < 1)
            {
                return Results.BadRequest(new { error = "A custom frequency needs an interval in days." });
            }
        }
        else if (request.Frequency.Months() is null)
        {
            return Results.BadRequest(new { error = "Unknown frequency." });
        }

        if (request.GraceDays is < 0 or > 90)
        {
            return Results.BadRequest(new { error = "Grace days must be between 0 and 90." });
        }

        if (request.NextDueDate.Year is < 2000 or > 2100)
        {
            return Results.BadRequest(new { error = "Give the date the next PM falls due." });
        }

        var intervalDays = request.Frequency == PmFrequency.Custom ? request.IntervalDays : 0;
        var patternChanged = schedule.Frequency != request.Frequency
            || schedule.IntervalDays != intervalDays
            || schedule.AnchorDate != request.NextDueDate;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (patternChanged || !request.IsActive)
        {
            // Only what is ahead and untouched: Scheduled means not yet due and not done.
            await db.PmTasks
                .Where(t => t.PmScheduleId == id && t.Status == PmTaskStatus.Scheduled)
                .ExecuteDeleteAsync(ct);
        }

        schedule.Frequency = request.Frequency;
        schedule.IntervalDays = intervalDays;
        schedule.AnchorDate = request.NextDueDate;
        schedule.GraceDays = request.GraceDays;
        schedule.IsActive = request.IsActive;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (schedule.IsActive)
        {
            await generator.RunAsync(ct);
        }

        return Results.NoContent();
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
