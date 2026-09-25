using HospitalPm.Domain.Maintenance;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Equipment;

/// <summary>
/// Everything known about one machine, in one call.
///
/// This is the question a QR scan actually asks: which machine is this, and
/// what has been done to it. Answering it from four separate requests means
/// four round trips and a client stitching them together, on a hospital PC
/// that is also running the database.
/// </summary>
public static class EquipmentHistoryEndpoints
{
    /// <summary>
    /// A machine in service for fifteen years accumulates a lot of history.
    /// The page shows the recent past; the PM and work order lists are there
    /// for the full record.
    /// </summary>
    private const int MaxHistoryRows = 50;

    public static void MapEquipmentHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/equipment/{id:int}/history", HistoryAsync)
            .WithTags("Equipment")
            // Every role: a technician standing at the machine is exactly who
            // needs to know what was last done to it.
            .RequireAuthorization();
    }

    private static async Task<IResult> HistoryAsync(
        int id,
        HospitalPmDbContext db,
        HospitalPm.Infrastructure.Maintenance.HospitalClock clock,
        CancellationToken ct)
    {
        var equipment = await db.Equipment.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new
            {
                e.Id,
                e.AssetTag,
                e.SerialNumber,
                e.Manufacturer,
                e.Model,
                e.Status,
                e.Criticality,
                e.PurchaseDate,
                e.InstallationDate,
                e.WarrantyExpiryDate,
                e.Notes,
                e.EquipmentTypeId,
                EquipmentTypeName = e.EquipmentType!.Name,
                e.LocationId,
                LocationName = e.Location!.Name,
                LocationPath = e.Location!.Path,
            })
            .SingleOrDefaultAsync(ct);

        if (equipment is null)
        {
            return Results.NotFound();
        }

        // The breadcrumb a technician needs: "Ward 3" alone does not say
        // which building, and two hospitals in a group can both have one.
        var ancestorIds = equipment.LocationPath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0)
            .Where(n => n > 0)
            .ToList();

        var ancestors = await db.Locations.AsNoTracking()
            .Where(l => ancestorIds.Contains(l.Id))
            .OrderBy(l => l.Depth)
            .Select(l => new { l.Id, l.Name, Level = (int)l.Level })
            .ToListAsync(ct);

        var today = clock.Today();

        var openPm = await db.PmTasks.AsNoTracking()
            .Where(t => t.EquipmentId == id
                        && t.Status != PmTaskStatus.Completed
                        && t.Status != PmTaskStatus.Skipped)
            .OrderBy(t => t.DueDate)
            .Select(t => new
            {
                t.Id,
                t.DueDate,
                t.Status,
                ChecklistName = t.Schedule!.ChecklistTemplate!.Name,
                DaysLate = t.DueDate < today ? today.DayNumber - t.DueDate.DayNumber : 0,
            })
            .ToListAsync(ct);

        var closedPm = await db.PmTasks.AsNoTracking()
            .Where(t => t.EquipmentId == id
                        && (t.Status == PmTaskStatus.Completed || t.Status == PmTaskStatus.Skipped))
            .OrderByDescending(t => t.CompletedAtUtc)
            .Take(MaxHistoryRows)
            .Select(t => new
            {
                t.Id,
                t.DueDate,
                t.Status,
                t.CompletedAtUtc,
                t.SkipReason,
                ChecklistName = t.Schedule!.ChecklistTemplate!.Name,
                CompletedBy = db.Users
                    .Where(u => u.Id == t.CompletedByUserId)
                    .Select(u => u.FullName)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        // Out-of-range counts are computed here rather than in the query
        // above. Answers is a jsonb-converted dictionary, so EF cannot
        // translate a predicate over its contents into SQL — it throws at
        // runtime rather than falling back. One extra query for at most
        // fifty rows is the right trade.
        var closedIds = closedPm.Select(t => t.Id).ToList();

        var completions = await db.PmCompletions.AsNoTracking()
            .Where(c => closedIds.Contains(c.PmTaskId))
            .ToListAsync(ct);

        var byTask = completions.ToDictionary(c => c.PmTaskId);

        var completedPm = closedPm.Select(t => new
        {
            t.Id,
            t.DueDate,
            t.Status,
            t.CompletedAtUtc,
            t.SkipReason,
            t.ChecklistName,
            t.CompletedBy,
            // Surfaced on the list itself: a PM that passed with a reading
            // out of spec is the row someone needs to open.
            OutOfRange = byTask.TryGetValue(t.Id, out var c) ? c.OutOfRangeCount : 0,
            FailedChecks = byTask.TryGetValue(t.Id, out var f) ? f.FailedCheckCount : 0,
            HasCertificate = byTask.ContainsKey(t.Id),
        }).ToList();

        var workOrders = await db.WorkOrders.AsNoTracking()
            .Where(w => w.EquipmentId == id)
            .OrderByDescending(w => w.ReportedAtUtc)
            .Take(MaxHistoryRows)
            .Select(w => new
            {
                w.Id,
                w.Number,
                w.Status,
                w.Priority,
                w.FaultDescription,
                w.ReportedAtUtc,
                w.ResolvedAtUtc,
                w.ResolutionNotes,
                w.OutOfServiceAtUtc,
                w.BackInServiceAtUtc,
            })
            .ToListAsync(ct);

        var downtimeMinutes = workOrders
            .Where(w => w.OutOfServiceAtUtc is not null && w.BackInServiceAtUtc is not null)
            .Sum(w => (int)(w.BackInServiceAtUtc!.Value - w.OutOfServiceAtUtc!.Value).TotalMinutes);

        return Results.Ok(new
        {
            equipment,
            breadcrumb = ancestors,
            openPm,
            completedPm,
            workOrders,
            summary = new
            {
                openPmCount = openPm.Count,
                overduePmCount = openPm.Count(t => t.Status == PmTaskStatus.Overdue),
                completedPmCount = await db.PmTasks.CountAsync(
                    t => t.EquipmentId == id && t.Status == PmTaskStatus.Completed, ct),
                openWorkOrderCount = workOrders.Count(w =>
                    w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Cancelled),
                totalWorkOrderCount = await db.WorkOrders.CountAsync(w => w.EquipmentId == id, ct),
                // Recorded downtime across every closed fault. The number a
                // hospital is asked for when it reports equipment uptime.
                totalDowntimeMinutes = downtimeMinutes,
                currentlyDown = workOrders.Any(w =>
                    w.OutOfServiceAtUtc is not null && w.BackInServiceAtUtc is null),
            },
        });
    }
}
