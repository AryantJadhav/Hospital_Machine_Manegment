namespace HospitalPm.Domain.WorkOrders;

/// <summary>One stretch of time a machine was down. Still down while <paramref name="ToUtc"/> is null.</summary>
public readonly record struct DowntimeWindow(DateTime FromUtc, DateTime? ToUtc);

/// <summary>
/// How long a machine was down, and so how long it was up.
///
/// A machine is down from the moment someone reports it is not working until it is back in use. Two
/// reports of the same machine can overlap, and the hours they share are one outage, not two, so
/// the windows are merged before they are added up.
/// </summary>
public static class Downtime
{
    /// <summary>
    /// Minutes down, counting a window that is still open up to <paramref name="nowUtc"/>.
    /// With <paramref name="sinceUtc"/>, only the part of each window after that moment counts.
    /// </summary>
    public static double Minutes(IEnumerable<DowntimeWindow> windows, DateTime nowUtc, DateTime? sinceUtc = null)
    {
        var spans = windows
            .Select(w => (
                From: sinceUtc is { } s && s > w.FromUtc ? s : w.FromUtc,
                To: w.ToUtc is { } to && to < nowUtc ? to : nowUtc))
            .Where(s => s.To > s.From)
            .OrderBy(s => s.From)
            .ToList();

        double total = 0;
        DateTime? from = null;
        DateTime to2 = default;

        foreach (var span in spans)
        {
            if (from is null)
            {
                (from, to2) = (span.From, span.To);
            }
            else if (span.From <= to2)
            {
                if (span.To > to2)
                {
                    to2 = span.To;
                }
            }
            else
            {
                total += (to2 - from.Value).TotalMinutes;
                (from, to2) = (span.From, span.To);
            }
        }

        if (from is not null)
        {
            total += (to2 - from.Value).TotalMinutes;
        }

        return total;
    }

    /// <summary>Hours to two places: "5.25", which is what a reader wants, not "315 minutes".</summary>
    public static double Hours(double minutes) => Math.Round(minutes / 60.0, 2);
}
