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
/// Which machines were down in a period and for how long.
///
/// Administrators only, like the compliance report: it is the biomedical head's view of the
/// whole fleet.
/// </summary>
public static class DowntimeReportEndpoints
{
    public static void MapDowntimeReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports/downtime")
            .WithTags("Reports")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        group.MapGet("/", SummaryAsync);
        group.MapGet("/report.csv", CsvAsync);
    }

    private static async Task<IResult> SummaryAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int? locationId,
        CancellationToken ct)
    {
        var (scope, report, error) = await BuildAsync(db, clock, from, to, locationId, ct);
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
            machinesAffected = report.Machines.Count,
            incidents = report.Incidents,
            totalDowntimeHours = report.TotalDowntimeHours,
            stillDown = report.StillDown,
            machines = report.Machines,
        });
    }

    private static async Task<IResult> CsvAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int? locationId,
        CancellationToken ct)
    {
        var (_, report, error) = await BuildAsync(db, clock, from, to, locationId, ct);
        if (error is not null)
        {
            return error;
        }

        return Results.File(
            Encoding.UTF8.GetBytes(DowntimeReport.ToCsv(report!)),
            "text/csv; charset=utf-8",
            $"downtime-{report!.From:yyyyMMdd}-{report.To:yyyyMMdd}.csv");
    }

    private static async Task<(ReportScope? Scope, DowntimeReportData? Report, IResult? Error)> BuildAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        DateOnly? from,
        DateOnly? to,
        int? locationId,
        CancellationToken ct)
    {
        var (scope, error) = await ReportScope.ResolveAsync(db, clock, from, to, locationId, ct);
        if (error is not null)
        {
            return (null, null, error);
        }

        var startUtc = scope!.StartUtc;
        var endUtc = scope.EndUtc;

        // A report cancelled as raised in error is no outage, the same rule the machine's own
        // page uses.
        var query = db.WorkOrders.AsNoTracking()
            .Where(w => w.OutOfServiceAtUtc != null && w.Status != WorkOrderStatus.Cancelled)
            .Where(w => w.OutOfServiceAtUtc < endUtc && (w.BackInServiceAtUtc == null || w.BackInServiceAtUtc > startUtc));

        if (scope.PathPrefix is { } prefix)
        {
            query = query.Where(w => w.Equipment!.Location!.Path.StartsWith(prefix));
        }

        var rows = await query
            .Select(w => new
            {
                w.EquipmentId,
                From = w.OutOfServiceAtUtc!.Value,
                To = w.BackInServiceAtUtc,
                Tag = w.Equipment!.AssetTag,
                Type = w.Equipment!.EquipmentType!.Name,
                Place = w.Equipment!.Location!.Name,
                Registered = w.Equipment!.CreatedAtUtc,
            })
            .ToListAsync(ct);

        var inputs = rows
            .GroupBy(r => r.EquipmentId)
            .Select(g =>
            {
                var first = g.First();
                return new DowntimeInput(
                    g.Key,
                    first.Tag,
                    first.Type,
                    first.Place,
                    first.Registered,
                    g.Select(r => new DowntimeWindow(r.From, r.To)).ToList());
            })
            .ToList();

        return (scope, DowntimeReport.Build(inputs, scope.From, scope.To, startUtc, endUtc), null);
    }
}
