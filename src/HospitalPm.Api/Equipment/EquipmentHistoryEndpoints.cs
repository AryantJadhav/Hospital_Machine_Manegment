using HospitalPm.Domain.Identity;
using HospitalPm.Api.Auth;
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
            .RequirePermission(Permissions.RegisterView);
    }

    private static async Task<IResult> HistoryAsync(
        int id,
        HospitalPmDbContext db,
        HospitalPm.Infrastructure.Maintenance.HospitalClock clock,
        TimeProvider time,
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
                e.IsInsured,
                e.InsuranceProvider,
                e.InsurancePolicyNumber,
                e.InsuranceExpiryDate,
                e.PurchaseCost,
                e.InsuranceCost,
                e.MaintenanceContractType,
                e.MaintenanceVendor,
                e.MaintenanceContractNumber,
                e.MaintenanceStartDate,
                e.MaintenanceEndDate,
                e.MaintenanceCost,
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
                ChecklistName = t.Schedule!.ChecklistTemplate!.Name ?? "PM",
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
                ChecklistName = t.Schedule!.ChecklistTemplate!.Name ?? "PM",
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

        var reportCounts = await db.PmTaskAttachments.AsNoTracking()
            .Where(a => closedIds.Contains(a.PmTaskId))
            .GroupBy(a => a.PmTaskId)
            .Select(g => new { TaskId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TaskId, x => x.Count, ct);

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
            // Only a PM filled in on a checklist has a certificate. The vendor's report, or a PM
            // simply recorded as done, is its own record.
            HasCertificate = byTask.TryGetValue(t.Id, out var cert)
                && cert.PerformedBy == PmPerformedBy.InHouse && cert.ChecklistTemplateVersionId != null,
            PerformedBy = byTask.TryGetValue(t.Id, out var by) ? by.PerformedBy : PmPerformedBy.InHouse,
            VendorName = byTask.TryGetValue(t.Id, out var vn) ? vn.VendorName : null,
            ReportFiles = reportCounts.GetValueOrDefault(t.Id),
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

        // Down from the report until the machine is back in use, over every report ever made of it (not
        // just the rows shown), overlaps counted once. A report cancelled as raised in error is no outage.
        var now = time.GetUtcNow().UtcDateTime;
        var windows = (await db.WorkOrders.AsNoTracking()
                .Where(w => w.EquipmentId == id && w.OutOfServiceAtUtc != null && w.Status != WorkOrderStatus.Cancelled)
                .Select(w => new { w.OutOfServiceAtUtc, w.BackInServiceAtUtc })
                .ToListAsync(ct))
            .Select(w => new DowntimeWindow(w.OutOfServiceAtUtc!.Value, w.BackInServiceAtUtc))
            .ToList();

        var registeredAt = await db.Equipment.AsNoTracking()
            .Where(e => e.Id == id).Select(e => e.CreatedAtUtc).SingleAsync(ct);

        // Uptime is counted from the last 30 days, or from when the machine was put on the register if that
        // is more recent: a machine added last week has had a week, not a month, to be up.
        var since = now.AddDays(-30) > registeredAt ? now.AddDays(-30) : registeredAt;
        var trackedMinutes = Math.Max(0, (now - since).TotalMinutes);
        var downLast30 = Math.Min(trackedMinutes, Downtime.Minutes(windows, now, since));
        var upLast30 = trackedMinutes - downLast30;

        return Results.Ok(new
        {
            equipment,
            breadcrumb = ancestors,
            openPm,
            completedPm,
            workOrders = workOrders.Select(w => new
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
                DowntimeHours = w.OutOfServiceAtUtc is { } d && w.Status != WorkOrderStatus.Cancelled
                    ? Downtime.Hours(Downtime.Minutes([new DowntimeWindow(d, w.BackInServiceAtUtc)], now))
                    : (double?)null,
                StillDown = w.OutOfServiceAtUtc != null && w.BackInServiceAtUtc == null
                    && w.Status != WorkOrderStatus.Cancelled,
            }),
            summary = new
            {
                openPmCount = openPm.Count,
                overduePmCount = openPm.Count(t => t.Status == PmTaskStatus.Overdue),
                completedPmCount = await db.PmTasks.CountAsync(
                    t => t.EquipmentId == id && t.Status == PmTaskStatus.Completed, ct),
                openWorkOrderCount = workOrders.Count(w =>
                    w.Status != WorkOrderStatus.Resolved
                    && w.Status != WorkOrderStatus.Closed
                    && w.Status != WorkOrderStatus.Cancelled),
                totalWorkOrderCount = await db.WorkOrders.CountAsync(w => w.EquipmentId == id, ct),
                // Hours down since the machine was put on the register, and the last 30 days split into
                // down and up. The numbers a hospital is asked for when it reports equipment uptime.
                totalDowntimeHours = Downtime.Hours(Downtime.Minutes(windows, now)),
                downtimeHoursLast30Days = Downtime.Hours(downLast30),
                uptimeHoursLast30Days = Downtime.Hours(upLast30),
                availabilityPercentLast30Days = trackedMinutes > 0
                    ? Math.Round(100.0 * upLast30 / trackedMinutes, 1)
                    : (double?)null,
                currentlyDown = windows.Any(w => w.ToUtc is null),
            },
        });
    }
}
