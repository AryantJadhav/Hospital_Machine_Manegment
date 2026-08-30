namespace HospitalPm.Domain.Maintenance;

/// <summary>
/// Works out when a PM falls due.
///
/// Pure functions with no clock and no database, because this is the piece
/// that decides whether a hospital's compliance report says four PMs a year
/// or three.
/// </summary>
public static class PmDueDates
{
    /// <summary>
    /// The occurrence <paramref name="index"/> steps after the anchor.
    ///
    /// Dates are anchored to the schedule's start, NOT to when the last PM
    /// was actually done. This is the decision that matters most here.
    ///
    /// If a quarterly PM due 1 April is completed on 20 April, the next one
    /// is due 1 July — not 20 July. Anchoring to completion lets the schedule
    /// drift later every cycle, and a hospital that intends four PMs a year
    /// quietly ends up doing three. An auditor counts occurrences against the
    /// calendar, so the calendar is what the schedule follows.
    ///
    /// Month arithmetic clamps to the end of shorter months: a monthly PM
    /// anchored to the 31st falls on the 30th in April and the 28th in
    /// February, then returns to the 31st in May rather than sliding earlier
    /// every month.
    /// </summary>
    public static DateOnly Occurrence(DateOnly anchor, PmFrequency frequency, int intervalDays, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        if (frequency == PmFrequency.Custom)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(intervalDays, 1);
            return anchor.AddDays(intervalDays * index);
        }

        var months = frequency.Months()
            ?? throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unknown frequency.");

        // AddMonths already clamps 31 Jan + 1 month to 28/29 Feb, and because
        // every occurrence is computed from the anchor rather than from the
        // previous occurrence, the clamping never accumulates.
        return anchor.AddMonths(months * index);
    }

    /// <summary>
    /// The first occurrence strictly after <paramref name="after"/>.
    ///
    /// Used to move a schedule on once a PM is completed, and to recover a
    /// schedule that has been neglected: rather than generating a year of
    /// missed occurrences, it skips to the next real one. The missed ones are
    /// already recorded as overdue tasks, so nothing is lost by not
    /// duplicating them.
    /// </summary>
    public static DateOnly NextAfter(DateOnly anchor, PmFrequency frequency, int intervalDays, DateOnly after)
    {
        if (after < anchor)
        {
            return anchor;
        }

        if (frequency == PmFrequency.Custom)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(intervalDays, 1);

            var elapsed = after.DayNumber - anchor.DayNumber;
            var steps = (elapsed / intervalDays) + 1;
            return anchor.AddDays(intervalDays * steps);
        }

        var months = frequency.Months()
            ?? throw new ArgumentOutOfRangeException(nameof(frequency), frequency, "Unknown frequency.");

        // Estimated, then corrected. A month is not a fixed number of days,
        // so stepping is the only way to be exact at the boundaries.
        var approx = (((after.Year - anchor.Year) * 12) + after.Month - anchor.Month) / months;
        var index = Math.Max(0, approx);

        while (Occurrence(anchor, frequency, intervalDays, index) <= after)
        {
            index++;
        }

        return Occurrence(anchor, frequency, intervalDays, index);
    }

    /// <summary>
    /// Every occurrence from the anchor up to and including
    /// <paramref name="horizon"/>, starting at <paramref name="from"/>.
    ///
    /// Capped so a schedule with a one-day interval and a start date years in
    /// the past cannot generate a million rows in one job run.
    /// </summary>
    public static IEnumerable<DateOnly> Between(
        DateOnly anchor,
        PmFrequency frequency,
        int intervalDays,
        DateOnly from,
        DateOnly horizon,
        int max = 500)
    {
        if (horizon < anchor)
        {
            yield break;
        }

        var index = 0;
        var produced = 0;

        while (produced < max)
        {
            var due = Occurrence(anchor, frequency, intervalDays, index);
            index++;

            if (due > horizon)
            {
                yield break;
            }

            if (due < from)
            {
                continue;
            }

            produced++;
            yield return due;
        }
    }
}
