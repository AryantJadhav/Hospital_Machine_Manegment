using HospitalPm.Domain.Maintenance;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Pure date arithmetic, no database. This is the code that decides whether
/// a hospital's compliance report says four PMs a year or three.
/// </summary>
public sealed class PmDueDateTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    // ---------------- anchoring ----------------

    [Fact]
    public void Occurrences_follow_the_calendar_not_the_completion_date()
    {
        var anchor = D(2026, 1, 1);

        // A quarterly PM due 1 April that is actually done on 20 April must
        // still put the next one on 1 July. Anchoring to the completion lets
        // the schedule drift later every cycle, and a hospital intending four
        // PMs a year quietly ends up doing three.
        Assert.Equal(D(2026, 4, 1), PmDueDates.Occurrence(anchor, PmFrequency.Quarterly, 0, 1));
        Assert.Equal(D(2026, 7, 1), PmDueDates.Occurrence(anchor, PmFrequency.Quarterly, 0, 2));
        Assert.Equal(D(2026, 10, 1), PmDueDates.Occurrence(anchor, PmFrequency.Quarterly, 0, 3));
        Assert.Equal(D(2027, 1, 1), PmDueDates.Occurrence(anchor, PmFrequency.Quarterly, 0, 4));
    }

    [Fact]
    public void Four_quarterly_occurrences_land_in_every_calendar_year()
    {
        var anchor = D(2026, 2, 15);

        for (var year = 0; year < 5; year++)
        {
            var inYear = Enumerable.Range(year * 4, 4)
                .Select(i => PmDueDates.Occurrence(anchor, PmFrequency.Quarterly, 0, i))
                .Count(d => d.Year == 2026 + year);

            // The property an auditor actually checks.
            Assert.Equal(4, inYear);
        }
    }

    // ---------------- month-end clamping ----------------

    [Fact]
    public void A_month_end_anchor_clamps_without_drifting_earlier()
    {
        var anchor = D(2026, 1, 31);

        Assert.Equal(D(2026, 2, 28), PmDueDates.Occurrence(anchor, PmFrequency.Monthly, 0, 1));
        Assert.Equal(D(2026, 3, 31), PmDueDates.Occurrence(anchor, PmFrequency.Monthly, 0, 2));
        Assert.Equal(D(2026, 4, 30), PmDueDates.Occurrence(anchor, PmFrequency.Monthly, 0, 3));

        // The important one: May returns to the 31st. Computing each date
        // from the previous one instead of the anchor would have pinned this
        // to the 28th forever after February.
        Assert.Equal(D(2026, 5, 31), PmDueDates.Occurrence(anchor, PmFrequency.Monthly, 0, 4));
    }

    [Fact]
    public void A_leap_day_anchor_survives_non_leap_years()
    {
        var anchor = D(2028, 2, 29);

        Assert.Equal(D(2029, 2, 28), PmDueDates.Occurrence(anchor, PmFrequency.Yearly, 0, 1));
        Assert.Equal(D(2032, 2, 29), PmDueDates.Occurrence(anchor, PmFrequency.Yearly, 0, 4));
    }

    // ---------------- next-after ----------------

    [Fact]
    public void NextAfter_skips_a_neglected_schedule_forward()
    {
        var anchor = D(2026, 1, 1);

        // Two years untouched. It should return the next real occurrence, not
        // walk through eight missed ones — those are already recorded as
        // overdue tasks.
        var next = PmDueDates.NextAfter(anchor, PmFrequency.Quarterly, 0, after: D(2027, 12, 15));

        Assert.Equal(D(2028, 1, 1), next);
    }

    [Fact]
    public void NextAfter_is_strictly_after_the_given_date()
    {
        var anchor = D(2026, 1, 1);

        // Asked on the due date itself, the answer is the next one, not today.
        Assert.Equal(D(2026, 4, 1), PmDueDates.NextAfter(anchor, PmFrequency.Quarterly, 0, D(2026, 1, 1)));
    }

    [Fact]
    public void NextAfter_before_the_anchor_returns_the_anchor()
    {
        var anchor = D(2026, 6, 1);

        Assert.Equal(anchor, PmDueDates.NextAfter(anchor, PmFrequency.Quarterly, 0, D(2026, 1, 1)));
    }

    [Fact]
    public void Custom_intervals_step_in_days()
    {
        var anchor = D(2026, 1, 1);

        Assert.Equal(D(2026, 2, 15), PmDueDates.Occurrence(anchor, PmFrequency.Custom, 45, 1));
        Assert.Equal(D(2026, 4, 1), PmDueDates.Occurrence(anchor, PmFrequency.Custom, 45, 2));
        Assert.Equal(D(2026, 2, 15), PmDueDates.NextAfter(anchor, PmFrequency.Custom, 45, D(2026, 1, 20)));
    }

    // ---------------- horizon ----------------

    [Fact]
    public void Between_returns_only_dates_inside_the_window()
    {
        var anchor = D(2026, 1, 1);

        var dates = PmDueDates.Between(
            anchor, PmFrequency.Monthly, 0,
            from: D(2026, 3, 1), horizon: D(2026, 6, 30)).ToList();

        Assert.Equal([D(2026, 3, 1), D(2026, 4, 1), D(2026, 5, 1), D(2026, 6, 1)], dates);
    }

    [Fact]
    public void Between_is_capped_so_a_daily_schedule_cannot_flood_the_table()
    {
        var anchor = D(2000, 1, 1);

        // A one-day interval anchored 26 years ago would otherwise produce
        // nearly ten thousand rows in a single job run.
        var dates = PmDueDates.Between(
            anchor, PmFrequency.Custom, 1,
            from: anchor, horizon: D(2026, 12, 31), max: 500).ToList();

        Assert.Equal(500, dates.Count);
    }

    // ---------------- status ----------------

    [Theory]
    [InlineData(0, -1, PmTaskStatus.Scheduled)]  // due tomorrow
    [InlineData(0, 0, PmTaskStatus.Due)]         // due today, no grace
    [InlineData(0, 1, PmTaskStatus.Overdue)]     // one day past, no grace
    [InlineData(7, 1, PmTaskStatus.Due)]         // one day past, a week of grace
    [InlineData(7, 7, PmTaskStatus.Due)]         // last day of grace
    [InlineData(7, 8, PmTaskStatus.Overdue)]     // grace exhausted
    public void Status_respects_the_grace_window(int graceDays, int daysSinceDue, PmTaskStatus expected)
    {
        var due = D(2026, 6, 1);
        var today = due.AddDays(daysSinceDue);

        // Without grace, a PM is overdue one minute past midnight, and staff
        // learn to ignore the overdue list entirely.
        Assert.Equal(expected, PmTask.StatusOn(today, due, graceDays));
    }
}
