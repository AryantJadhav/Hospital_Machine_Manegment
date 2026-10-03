using System.Globalization;
using System.Security.Claims;
using System.Text;
using HospitalPm.Api.Auth;
using HospitalPm.Api.Reports;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Export;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// See EquipmentEndpoints: ToLower() is what translates in LINQ-to-SQL.
#pragma warning disable CA1862

namespace HospitalPm.Api.WorkOrders;

/// <summary>
/// The repairs that have been done: what was wrong, what was done about it, and who did it, for a
/// period. It is the history a department asks for ("what has been done on our machines?"), and it
/// is limited like everything else a person from another department reads: whatever the request
/// is asked of, the other departments' work is not there (see HospitalPmDbContext.RestrictTo).
///
/// No money: the cost of parts is the biomedical department's figure, and is not in this report.
/// </summary>
public static class ServiceHistoryEndpoints
{
    private const int MaxPageSize = 200;

    /// <summary>The period shown when none is asked for: the last three months.</summary>
    private const int DefaultDays = 90;

    /// <summary>
    /// Minutes the machine was out of service, when both ends are known. The same rule as the work
    /// order's own DowntimeMinutes, which is computed rather than stored and so cannot be asked of the
    /// database.
    /// </summary>
    private static int? DownMinutes(DateTime? outOfService, DateTime? backInService) =>
        outOfService is { } from && backInService is { } to && to > from ? (int)(to - from).TotalMinutes : null;

    public static void MapServiceHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/work-orders/history")
            .WithTags("Service history")
            .RequirePermission(Permissions.WorkOrdersView);

        group.MapGet("/", ListAsync);
        group.MapGet("/report.csv", CsvAsync);
    }

    private static async Task<(IQueryable<WorkOrder>? Query, ReportScope? Scope, IResult? Error)> BuildAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal principal,
        DateOnly? from,
        DateOnly? to,
        string? q,
        string? requestedBy,
        CancellationToken ct)
    {
        var today = clock.Today();
        var (scope, error) = await ReportScope.ResolveAsync(
            db, clock, from ?? today.AddDays(-DefaultDays), to ?? today, null, ct);
        if (error is not null)
        {
            return (null, null, error);
        }

        var startUtc = scope!.StartUtc;
        var endUtc = scope.EndUtc;

        // Resolved or closed: the repair is done. One reopened and not yet fixed again is not history.
        var query = db.WorkOrders.AsNoTracking()
            .Where(w => (w.Status == WorkOrderStatus.Resolved || w.Status == WorkOrderStatus.Closed)
                        && w.ResolvedAtUtc != null
                        && w.ResolvedAtUtc >= startUtc
                        && w.ResolvedAtUtc < endUtc);

        if (string.Equals(requestedBy, "me", StringComparison.OrdinalIgnoreCase))
        {
            var me = PermissionService.UserIdOf(principal);
            query = query.Where(w => w.ReportedByUserId == me);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLowerInvariant();
            query = query.Where(w =>
                w.Number.ToLower().Contains(term) ||
                w.Equipment!.AssetTag.ToLower().Contains(term) ||
                w.Equipment!.EquipmentType!.Name.ToLower().Contains(term) ||
                w.FaultDescription.ToLower().Contains(term) ||
                (w.ResolutionNotes != null && w.ResolutionNotes.ToLower().Contains(term)));
        }

        return (query, scope, null);
    }

    private static async Task<IResult> ListAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal principal,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? q,
        [FromQuery] string? requestedBy,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var (query, scope, error) = await BuildAsync(db, clock, principal, from, to, q, requestedBy, ct);
        if (error is not null)
        {
            return error;
        }

        var total = await query!.CountAsync(ct);
        var windows = await query!
            .Select(w => new { w.OutOfServiceAtUtc, w.BackInServiceAtUtc })
            .ToListAsync(ct);
        long downtimeMinutes = windows.Sum(w => (long)(DownMinutes(w.OutOfServiceAtUtc, w.BackInServiceAtUtc) ?? 0));

        var rows = await query!
            .OrderByDescending(w => w.ResolvedAtUtc)
            .ThenByDescending(w => w.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(w => new
            {
                w.Id,
                w.Number,
                w.Status,
                w.Priority,
                w.EquipmentId,
                w.Equipment!.AssetTag,
                EquipmentTypeName = w.Equipment!.EquipmentType!.Name,
                LocationName = w.Equipment!.Location!.Name,
                w.FaultDescription,
                BreakdownType = (int?)w.BreakdownType,
                w.ResolutionNotes,
                w.ReportedAtUtc,
                ReportedByName = db.Users.Where(u => u.Id == w.ReportedByUserId).Select(u => u.FullName).FirstOrDefault(),
                w.ResolvedAtUtc,
                ResolvedByName = db.Users.Where(u => u.Id == w.ResolvedByUserId).Select(u => u.FullName).FirstOrDefault(),
                w.OutOfServiceAtUtc,
                w.BackInServiceAtUtc,
            })
            .ToListAsync(ct);

        var shown = rows.Select(r => new
        {
            r.Id,
            r.Number,
            r.Status,
            r.Priority,
            r.EquipmentId,
            r.AssetTag,
            r.EquipmentTypeName,
            r.LocationName,
            r.FaultDescription,
            r.BreakdownType,
            BreakdownTypeLabel = HospitalPm.Domain.WorkOrders.BreakdownTypeWords.Label(r.BreakdownType),
            r.ResolutionNotes,
            r.ReportedAtUtc,
            r.ReportedByName,
            r.ResolvedAtUtc,
            r.ResolvedByName,
            DowntimeMinutes = DownMinutes(r.OutOfServiceAtUtc, r.BackInServiceAtUtc),
        });

        return Results.Ok(new
        {
            from = scope!.From,
            to = scope.To,
            done = total,
            // How long the machines were out of service, over every repair in the period.
            downtimeHours = Math.Round(downtimeMinutes / 60.0, 1),
            items = shown,
            total,
            page,
            pageSize,
        });
    }

    private static async Task<IResult> CsvAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal principal,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? q,
        [FromQuery] string? requestedBy,
        CancellationToken ct)
    {
        var (query, scope, error) = await BuildAsync(db, clock, principal, from, to, q, requestedBy, ct);
        if (error is not null)
        {
            return error;
        }

        var rows = await query!
            .OrderByDescending(w => w.ResolvedAtUtc)
            .ThenByDescending(w => w.Id)
            .Take(10_000)
            .Select(w => new
            {
                w.Number,
                w.Equipment!.AssetTag,
                EquipmentTypeName = w.Equipment!.EquipmentType!.Name,
                LocationName = w.Equipment!.Location!.Name,
                w.FaultDescription,
                BreakdownType = (int?)w.BreakdownType,
                w.ResolutionNotes,
                w.ReportedAtUtc,
                ReportedByName = db.Users.Where(u => u.Id == w.ReportedByUserId).Select(u => u.FullName).FirstOrDefault(),
                w.ResolvedAtUtc,
                ResolvedByName = db.Users.Where(u => u.Id == w.ResolvedByUserId).Select(u => u.FullName).FirstOrDefault(),
                w.OutOfServiceAtUtc,
                w.BackInServiceAtUtc,
            })
            .ToListAsync(ct);

        string Local(DateTime? utc) =>
            utc is { } t ? (t + clock.Offset).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) : string.Empty;

        var csv = new StringBuilder();
        csv.AppendLine(Csv.Line(
        [
            "Request", "Machine number", "Machine", "Where", "What was wrong", "Breakdown type", "What was done",
            "Reported", "Requested by", "Repaired", "Repaired by", "Machine down (hours)",
        ]));

        foreach (var r in rows)
        {
            csv.AppendLine(Csv.Line(
            [
                r.Number, r.AssetTag, r.EquipmentTypeName, r.LocationName, r.FaultDescription,
                HospitalPm.Domain.WorkOrders.BreakdownTypeWords.Label(r.BreakdownType), r.ResolutionNotes,
                Local(r.ReportedAtUtc), r.ReportedByName, Local(r.ResolvedAtUtc), r.ResolvedByName,
                DownMinutes(r.OutOfServiceAtUtc, r.BackInServiceAtUtc) is { } m ? (m / 60.0).ToString("0.0", CultureInfo.InvariantCulture) : string.Empty,
            ]));
        }

        return Results.File(
            Encoding.UTF8.GetBytes(csv.ToString()),
            "text/csv; charset=utf-8",
            $"service-history-{scope!.From:yyyyMMdd}-{scope.To:yyyyMMdd}.csv");
    }
}
