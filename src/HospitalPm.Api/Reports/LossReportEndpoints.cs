using System.Text;
using HospitalPm.Api.Auth;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Losses;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using HospitalPm.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Reports;

/// <summary>
/// The machines lost in a period: how many, how they went, what they were worth, how many came back and how many were
/// written off, and where they went from.
///
/// Administrators only, like the other fleet reports. A loss belongs to the day the machine was found missing, at the
/// hospital, and to the place it was meant to be in. The same records the Loss of equipment pages keep; one written in
/// error is left out. About machines only: nothing here concerns a patient.
/// </summary>
public static class LossReportEndpoints
{
    public static void MapLossReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports/losses")
            .WithTags("Reports")
            // Reports are for those who may see reports, and losses are for the department, the engineers and the head: so both.
            .RequirePermission(Permissions.ReportsView)
            .RequirePermission(Permissions.LossesView);

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
            total = report.Total,
            open = report.Open,
            recovered = report.Recovered,
            writtenOff = report.WrittenOff,
            withPoliceComplaint = report.WithPoliceComplaint,
            valueReported = report.ValueReported,
            valueLost = report.ValueLost,
            valueRecovered = report.ValueRecovered,
            withoutValue = report.WithoutValue,
            byKind = report.ByKind,
            byLocation = report.ByLocation,
            repeatMachines = report.RepeatMachines,
            items = report.Items,
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
            Encoding.UTF8.GetBytes(LossFleetReport.ToCsv(report!)),
            "text/csv; charset=utf-8",
            $"losses-{report!.From:yyyyMMdd}-{report.To:yyyyMMdd}.csv");
    }

    private static async Task<(ReportScope? Scope, LossFleetData? Report, IResult? Error)> BuildAsync(
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

        var firstDay = scope!.From;
        var lastDay = scope.To;

        // A loss written up by mistake is not a loss: it is left out of every figure.
        var query = db.EquipmentLosses.AsNoTracking()
            .Where(l => l.Status != LossStatus.Cancelled && l.NoticedOn >= firstDay && l.NoticedOn <= lastDay);

        if (scope.PathPrefix is { } prefix)
        {
            query = query.Where(l => l.Location!.Path.StartsWith(prefix));
        }

        var rows = await query
            .Select(l => new LossFleetInput(
                l.Id,
                l.ReportedAtUtc,
                l.EquipmentId,
                l.Equipment!.AssetTag,
                l.Equipment.EquipmentType!.Name,
                l.Location == null ? null : l.Location.Name,
                l.Kind,
                l.Status,
                l.NoticedOn,
                l.LastSeenOn,
                l.RecoveredOn,
                l.EstimatedValue,
                l.PoliceReportNumber))
            .ToListAsync(ct);

        return (scope, LossFleetReport.Build(rows, firstDay, lastDay, clock.Today()), null);
    }
}
