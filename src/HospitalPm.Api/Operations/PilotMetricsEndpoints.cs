using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Operations;

/// <summary>
/// Weekly numbers for tracking a design partner pilot.
///
/// The dashboard shows today's snapshot; this shows week-over-week trends:
/// are PMs moving from paper to app, are more users logging in, is adoption
/// growing or plateauing. The build plan says to track "assets loaded, PMs
/// completed in-app vs on paper, scan failures, support calls" — the last
/// two are observed in person; the first two are queryable here.
///
/// Admin only. A technician seeing their own adoption numbers would change
/// their behaviour, which defeats the purpose of watching.
/// </summary>
public static class PilotMetricsEndpoints
{
    public static void MapPilotMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/pilot-metrics", MetricsAsync)
            .WithTags("Pilot")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }

    private static async Task<IResult> MetricsAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        CancellationToken ct)
    {
        var today = clock.Today();
        var utcNow = DateTime.UtcNow;

        // Current week: Monday to Sunday
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var weekStart = today.AddDays(-daysSinceMonday);
        var weekEnd = weekStart.AddDays(6);

        // Previous week
        var prevWeekStart = weekStart.AddDays(-7);

        // Current month
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        // Previous month
        var prevMonth = today.AddMonths(-1);
        var prevMonthStart = new DateOnly(prevMonth.Year, prevMonth.Month, 1);

        // Timestamps are stored in UTC but the weeks and months are the
        // hospital's. A PM signed at 01:00 on a Monday in India is still Sunday
        // in UTC, so the boundaries are converted once here rather than
        // truncating each timestamp to a UTC date.
        DateTime Utc(DateOnly localDay) =>
            localDay.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - clock.Offset;

        var weekStartUtc = Utc(weekStart);
        var nextWeekStartUtc = Utc(weekEnd.AddDays(1));
        var prevWeekStartUtc = Utc(prevWeekStart);
        var monthStartUtc = Utc(monthStart);
        var prevMonthStartUtc = Utc(prevMonthStart);
        var tomorrowUtc = Utc(today.AddDays(1));

        // --- Equipment ---
        var totalAssets = await db.Equipment.CountAsync(ct);

        // --- PM metrics ---
        var pmsThisWeek = await db.PmTasks.CountAsync(
            t => t.Status == PmTaskStatus.Completed
                 && t.CompletedAtUtc != null
                 && t.CompletedAtUtc >= weekStartUtc
                 && t.CompletedAtUtc < nextWeekStartUtc, ct);

        var pmsLastWeek = await db.PmTasks.CountAsync(
            t => t.Status == PmTaskStatus.Completed
                 && t.CompletedAtUtc != null
                 && t.CompletedAtUtc >= prevWeekStartUtc
                 && t.CompletedAtUtc < weekStartUtc, ct);

        var pmsThisMonth = await db.PmTasks.CountAsync(
            t => t.Status == PmTaskStatus.Completed
                 && t.CompletedAtUtc != null
                 && t.CompletedAtUtc >= monthStartUtc
                 && t.CompletedAtUtc < tomorrowUtc, ct);

        var pmsLastMonth = await db.PmTasks.CountAsync(
            t => t.Status == PmTaskStatus.Completed
                 && t.CompletedAtUtc != null
                 && t.CompletedAtUtc >= prevMonthStartUtc
                 && t.CompletedAtUtc < monthStartUtc, ct);

        // Compliance: of what fell due this month, how many got done
        var dueThisMonth = await db.PmTasks.CountAsync(
            t => t.DueDate >= monthStart && t.DueDate <= today, ct);
        var dueThisMonthDone = await db.PmTasks.CountAsync(
            t => t.DueDate >= monthStart && t.DueDate <= today
                 && t.Status == PmTaskStatus.Completed, ct);

        var overdue = await db.PmTasks.CountAsync(
            t => t.Status == PmTaskStatus.Overdue, ct);

        // --- Work orders ---
        var wosOpenedThisWeek = await db.WorkOrders.CountAsync(
            w => w.ReportedAtUtc >= weekStartUtc
                 && w.ReportedAtUtc < nextWeekStartUtc, ct);

        var wosResolvedThisWeek = await db.WorkOrders.CountAsync(
            w => w.ResolvedAtUtc != null
                 && w.ResolvedAtUtc >= weekStartUtc
                 && w.ResolvedAtUtc < nextWeekStartUtc, ct);

        var wosOpen = await db.WorkOrders.CountAsync(
            w => w.Status != WorkOrderStatus.Closed
                 && w.Status != WorkOrderStatus.Cancelled, ct);

        // --- Users ---
        // Active this week = logged in since the start of this week.
        // LastLoginAtUtc is stamped on every login, so this is a reliable
        // measure of who is actually opening the app.
        var totalUsers = await db.Users.CountAsync(ct);
        var activeThisWeek = await db.Users.CountAsync(
            u => u.LastLoginAtUtc != null && u.LastLoginAtUtc >= weekStartUtc, ct);
        var activeLastWeek = await db.Users.CountAsync(
            u => u.LastLoginAtUtc != null
                 && u.LastLoginAtUtc >= prevWeekStartUtc
                 && u.LastLoginAtUtc < weekStartUtc, ct);

        return Results.Ok(new
        {
            asOf = utcNow,
            weekLabel = $"{weekStart:yyyy-MM-dd} to {weekEnd:yyyy-MM-dd}",

            equipment = new
            {
                total = totalAssets,
            },

            pm = new
            {
                completedThisWeek = pmsThisWeek,
                completedLastWeek = pmsLastWeek,
                completedThisMonth = pmsThisMonth,
                completedLastMonth = pmsLastMonth,
                overdue,
                compliancePercent = dueThisMonth == 0
                    ? (int?)null
                    : (int)Math.Round(100.0 * dueThisMonthDone / dueThisMonth),
            },

            workOrders = new
            {
                openedThisWeek = wosOpenedThisWeek,
                resolvedThisWeek = wosResolvedThisWeek,
                currentlyOpen = wosOpen,
            },

            users = new
            {
                total = totalUsers,
                activeThisWeek,
                activeLastWeek,
            },
        });
    }
}
