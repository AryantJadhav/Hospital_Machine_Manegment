using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Reports;

/// <summary>
/// The period and place a fleet report is asked about, worked out once.
///
/// Dates are the hospital's days, so the period's edges are midnight at the hospital, not
/// midnight UTC. Counting stops at now when the period has not finished.
/// </summary>
internal sealed record ReportScope(
    DateOnly From,
    DateOnly To,
    DateTime StartUtc,
    DateTime EndUtc,
    DateOnly CountedThrough,
    string Scope,
    string? PathPrefix)
{
    /// <summary>A report for more than ten years is a mistake, not a request.</summary>
    private const int MaxDays = 3660;

    public static async Task<(ReportScope? Value, IResult? Error)> ResolveAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        DateOnly? fromParam,
        DateOnly? toParam,
        int? locationId,
        CancellationToken ct)
    {
        var today = clock.Today();

        // This month so far, when nothing is asked for.
        var from = fromParam ?? new DateOnly(today.Year, today.Month, 1);
        var to = toParam ?? today;

        if (to < from)
        {
            return (null, Results.BadRequest(new { error = "The end of the period is before its start." }));
        }

        if (to.DayNumber - from.DayNumber > MaxDays)
        {
            return (null, Results.BadRequest(new { error = "Choose a period of ten years or less." }));
        }

        var scope = "Whole hospital";
        string? prefix = null;

        if (locationId is not null)
        {
            var chosen = await db.Locations.AsNoTracking()
                .Where(l => l.Id == locationId)
                .Select(l => new { l.Name, l.Path })
                .SingleOrDefaultAsync(ct);

            if (chosen is null)
            {
                return (null, Results.NotFound(new { error = "Unknown location." }));
            }

            scope = chosen.Name;
            prefix = chosen.Path;
        }

        var startUtc = DateTime.SpecifyKind(from.ToDateTime(TimeOnly.MinValue) - clock.Offset, DateTimeKind.Utc);
        var endUtc = DateTime.SpecifyKind(to.AddDays(1).ToDateTime(TimeOnly.MinValue) - clock.Offset, DateTimeKind.Utc);
        var now = clock.UtcNow();

        return (
            new ReportScope(from, to, startUtc, endUtc < now ? endUtc : now, to > today ? today : to, scope, prefix),
            null);
    }
}
