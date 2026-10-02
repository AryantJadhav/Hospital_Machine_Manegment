using HospitalPm.Api.Auth;
using System.Text;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using HospitalPm.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Reports;

/// <summary>
/// The work finished in a period, and who did it: the faults resolved, what was done to each,
/// how long it took and what parts it used.
///
/// Administrators only, like the other fleet reports. A fault belongs to the day it was resolved,
/// at the hospital, so "today" is what was fixed today whenever it was reported.
/// </summary>
public static class WorkDoneReportEndpoints
{
    public static void MapWorkDoneReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports/work-done")
            .WithTags("Reports")
            .RequirePermission(Permissions.ReportsView);

        group.MapGet("/", SummaryAsync);
        group.MapGet("/report.csv", CsvAsync);
    }

    private static async Task<IResult> SummaryAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int? locationId,
        [FromQuery] int? userId,
        CancellationToken ct)
    {
        var (scope, report, error) = await BuildAsync(db, clock, from, to, locationId, userId, ct);
        if (error is not null)
        {
            return error;
        }

        return Results.Ok(new
        {
            from = report!.From,
            to = report.To,
            countedThrough = scope!.CountedThrough,
            scope = scope.Scope,
            done = report.Done,
            people = report.People.Count,
            averageHoursToFix = report.AverageHoursToFix,
            partsCost = report.PartsCost,
            byPerson = report.People,
            items = report.Items,
        });
    }

    private static async Task<IResult> CsvAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int? locationId,
        [FromQuery] int? userId,
        CancellationToken ct)
    {
        var (_, report, error) = await BuildAsync(db, clock, from, to, locationId, userId, ct);
        if (error is not null)
        {
            return error;
        }

        return Results.File(
            Encoding.UTF8.GetBytes(WorkDoneReport.ToCsv(report!)),
            "text/csv; charset=utf-8",
            $"work-done-{report!.From:yyyyMMdd}-{report.To:yyyyMMdd}.csv");
    }

    private static async Task<(ReportScope? Scope, WorkDoneReportData? Report, IResult? Error)> BuildAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        DateOnly? from,
        DateOnly? to,
        int? locationId,
        int? userId,
        CancellationToken ct)
    {
        // Today by default: the question this report is mostly asked is what was done today.
        var today = clock.Today();
        var (scope, error) = await ReportScope.ResolveAsync(db, clock, from ?? today, to ?? today, locationId, ct);
        if (error is not null)
        {
            return (null, null, error);
        }

        var startUtc = scope!.StartUtc;
        var endUtc = scope.EndUtc;

        // Resolved or closed: the fault is fixed. One reopened and not yet fixed again is not work
        // done, whatever it once was.
        var query = db.WorkOrders.AsNoTracking()
            .Where(w => (w.Status == WorkOrderStatus.Resolved || w.Status == WorkOrderStatus.Closed)
                        && w.ResolvedAtUtc != null
                        && w.ResolvedAtUtc >= startUtc
                        && w.ResolvedAtUtc < endUtc);

        if (scope.PathPrefix is { } prefix)
        {
            query = query.Where(w => w.Equipment!.Location!.Path.StartsWith(prefix));
        }

        if (userId is not null)
        {
            query = query.Where(w => w.ResolvedByUserId == userId);
        }

        var rows = await query
            .Select(w => new
            {
                w.Id,
                w.Number,
                w.EquipmentId,
                Tag = w.Equipment!.AssetTag,
                Type = w.Equipment!.EquipmentType!.Name,
                Place = w.Equipment!.Location!.Name,
                w.Priority,
                w.FaultDescription,
                w.ResolutionNotes,
                w.ResolvedByUserId,
                DoneBy = db.Users.Where(u => u.Id == w.ResolvedByUserId).Select(u => u.FullName).FirstOrDefault(),
                w.ReportedAtUtc,
                ResolvedAt = w.ResolvedAtUtc!.Value,
                w.OutOfServiceAtUtc,
                w.BackInServiceAtUtc,
            })
            .ToListAsync(ct);

        var ids = rows.Select(r => r.Id).ToList();

        var parts = (await db.WorkOrderParts.AsNoTracking()
                .Where(p => ids.Contains(p.WorkOrderId))
                .OrderBy(p => p.UsedAtUtc)
                .Select(p => new
                {
                    p.WorkOrderId,
                    Part = new WorkDonePart(p.SparePart!.PartNumber, p.SparePart.Name, p.QuantityUsed, p.UnitCostAtUse),
                })
                .ToListAsync(ct))
            .ToLookup(p => p.WorkOrderId, p => p.Part);

        var inputs = rows.Select(r => new WorkDoneInput(
            r.Id,
            r.Number,
            r.EquipmentId,
            r.Tag,
            r.Type,
            r.Place,
            r.Priority.ToString(),
            r.FaultDescription,
            r.ResolutionNotes,
            r.ResolvedByUserId,
            r.DoneBy,
            DateTime.SpecifyKind(r.ReportedAtUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(r.ResolvedAt, DateTimeKind.Utc),
            r.OutOfServiceAtUtc,
            r.BackInServiceAtUtc,
            parts[r.Id].ToList()));

        return (scope, WorkDoneReport.Build(inputs, scope.From, scope.To), null);
    }
}
