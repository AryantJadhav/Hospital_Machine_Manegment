using System.Text;
using HospitalPm.Api.Auth;
using HospitalPm.Api.WorkOrders;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using HospitalPm.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Reports;

/// <summary>
/// How quickly engineers responded to the faults raised in a period: from the fault being raised to an engineer pressing
/// In progress. Not the time to fix it. Judged against the times the head has set for each priority (so changing a time
/// changes how past faults are judged).
///
/// Administrators only, like the other fleet reports. A fault belongs to the day it was raised, at the hospital.
/// </summary>
public static class ResponseTimeEndpoints
{
    public static void MapResponseTimeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports/response-time")
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
            raised = report.Raised,
            responded = report.Responded,
            onTime = report.OnTime,
            late = report.Late,
            waiting = report.Waiting,
            overdue = report.Overdue,
            notJudged = report.NotJudged,
            onTimePercent = report.OnTimePercent,
            averageMinutes = report.AverageMinutes,
            medianMinutes = report.MedianMinutes,
            longestMinutes = report.LongestMinutes,
            byPriority = report.ByPriority,
            byPerson = report.People,
            items = report.Items.Select(i => new
            {
                i.WorkOrderId,
                i.Number,
                i.EquipmentId,
                i.AssetTag,
                i.EquipmentType,
                i.Location,
                i.Priority,
                i.ReportedAtUtc,
                i.StartedAtUtc,
                i.Responder,
                i.ResponseMinutes,
                i.TargetMinutes,
                i.OverdueMinutes,
                result = ResponseTimeReport.Result(i),
            }),
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
            Encoding.UTF8.GetBytes(ResponseTimeReport.ToCsv(report!)),
            "text/csv; charset=utf-8",
            $"response-time-{report!.From:yyyyMMdd}-{report.To:yyyyMMdd}.csv");
    }

    private static async Task<(ReportScope? Scope, ResponseTimeData? Report, IResult? Error)> BuildAsync(
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

        // Faults RAISED in the period, whatever has become of them since: answered, waiting, or never started.
        var query = db.WorkOrders.AsNoTracking()
            .Where(w => w.ReportedAtUtc >= startUtc && w.ReportedAtUtc < endUtc);

        if (scope.PathPrefix is { } prefix)
        {
            query = query.Where(w => w.Equipment!.Location!.Path.StartsWith(prefix));
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
                w.Status,
                w.ReportedAtUtc,
                w.StartedAtUtc,
                // The person who took it: only the assigned engineer may press In progress.
                w.AssignedToUserId,
                Responder = w.AssignedToUserId == null
                    ? null
                    : db.Users.Where(u => u.Id == w.AssignedToUserId).Select(u => u.FullName).FirstOrDefault(),
            })
            .ToListAsync(ct);

        var targets = await ResponseTargetEndpoints.LoadAsync(db, ct);

        var inputs = rows.Select(r => new ResponseInput(
            r.Id,
            r.Number,
            r.EquipmentId,
            r.Tag,
            r.Type,
            r.Place,
            r.Priority,
            r.Status,
            DateTime.SpecifyKind(r.ReportedAtUtc, DateTimeKind.Utc),
            r.StartedAtUtc is { } s ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : null,
            r.AssignedToUserId,
            r.Responder));

        return (scope, ResponseTimeReport.Build(inputs, targets, scope.From, scope.To, clock.UtcNow()), null);
    }
}
