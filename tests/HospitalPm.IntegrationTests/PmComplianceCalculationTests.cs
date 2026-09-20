using HospitalPm.Domain.Maintenance;
using HospitalPm.Infrastructure.Reports;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// What the compliance report counts, and when.
///
/// These are the figures an accreditation assessor is handed, so each rule is
/// pinned on its own: an off-by-one at a grace boundary, or a PM signed after
/// midnight counted on the wrong day, changes the number on the page.
/// </summary>
public sealed class PmComplianceCalculationTests
{
    private static readonly TimeSpan Ist = TimeSpan.FromMinutes(330);
    private static readonly DateOnly Today = new(2026, 9, 20);

    private static ComplianceTask Pm(
        DateOnly due,
        PmTaskStatus status = PmTaskStatus.Completed,
        DateTime? performedUtc = null,
        int grace = 0,
        string tag = "A-1",
        string type = "Ventilator",
        string department = "ICU",
        string? skipReason = null,
        int outOfRange = 0,
        int failed = 0) =>
        new(1, tag, type, "Room", department, "Quarterly PM", due, grace, status,
            performedUtc, "Tech", skipReason, outOfRange, failed);

    private static ComplianceLine Classify(ComplianceTask t, DateOnly? today = null) =>
        PmCompliance.Classify(t, today ?? Today, Ist);

    /// <summary>A moment that is <paramref name="local"/> on the hospital's clock.</summary>
    private static DateTime Ist_(DateOnly day, int hour, int minute = 0) =>
        day.ToDateTime(new TimeOnly(hour, minute)) - Ist;

    private static ComplianceReport Build(IReadOnlyList<ComplianceTask> tasks, DateOnly? to = null) =>
        PmCompliance.Build(
            tasks, new DateOnly(2026, 9, 1), to ?? new DateOnly(2026, 9, 30), Today, Ist,
            "Whole hospital", DateTime.UtcNow, "Head of Biomedical");

    // --- Done on time, or late ----------------------------------------------

    [Fact]
    public void Done_on_the_due_date_is_on_time()
    {
        var due = new DateOnly(2026, 9, 10);

        var line = Classify(Pm(due, performedUtc: Ist_(due, 11)));

        Assert.Equal(PmOutcome.OnTime, line.Outcome);
        Assert.Equal(0, line.DaysLate);
    }

    [Fact]
    public void Done_the_day_after_with_no_grace_is_late_by_one_day()
    {
        var due = new DateOnly(2026, 9, 10);

        var line = Classify(Pm(due, performedUtc: Ist_(due.AddDays(1), 9)));

        Assert.Equal(PmOutcome.Late, line.Outcome);
        Assert.Equal(1, line.DaysLate);
        Assert.Equal(due.AddDays(1), line.PerformedOn);
    }

    [Fact]
    public void Grace_days_extend_what_counts_as_on_time_to_the_last_day_and_not_beyond()
    {
        var due = new DateOnly(2026, 9, 10);

        Assert.Equal(PmOutcome.OnTime, Classify(Pm(due, grace: 3, performedUtc: Ist_(due.AddDays(3), 17))).Outcome);
        Assert.Equal(PmOutcome.Late, Classify(Pm(due, grace: 3, performedUtc: Ist_(due.AddDays(4), 9))).Outcome);
    }

    [Fact]
    public void Late_is_measured_from_the_due_date_not_from_the_end_of_grace()
    {
        var due = new DateOnly(2026, 9, 10);

        var line = Classify(Pm(due, grace: 3, performedUtc: Ist_(due.AddDays(6), 9)));

        Assert.Equal(6, line.DaysLate);
    }

    [Fact]
    public void The_day_a_PM_was_done_is_the_hospitals_day_not_UTCs()
    {
        // Signed at 00:30 on the 11th in India, which is 19:00 on the 10th in UTC.
        // Counted on UTC's calendar it would be on time; on the hospital's it is late.
        var due = new DateOnly(2026, 9, 10);
        var justAfterMidnight = Ist_(due.AddDays(1), 0, 30);

        Assert.Equal(new DateTime(2026, 9, 10, 19, 0, 0), justAfterMidnight);
        Assert.Equal(PmOutcome.Late, Classify(Pm(due, performedUtc: justAfterMidnight)).Outcome);

        // And the other way: 23:30 on the 10th in India is on time.
        Assert.Equal(PmOutcome.OnTime, Classify(Pm(due, performedUtc: Ist_(due, 23, 30))).Outcome);
    }

    // --- Not done -------------------------------------------------------------

    [Fact]
    public void A_skipped_PM_is_never_counted_as_done()
    {
        var line = Classify(Pm(new DateOnly(2026, 9, 5), PmTaskStatus.Skipped, skipReason: "Machine away for repair"));

        Assert.Equal(PmOutcome.Skipped, line.Outcome);
        Assert.Null(line.PerformedOn);

        var report = Build([Pm(new DateOnly(2026, 9, 5), PmTaskStatus.Skipped, skipReason: "Away")]);
        Assert.Equal(0, report.Totals.Completed);
        Assert.Equal(0.0, report.Totals.OnSchedulePercent);
        Assert.Equal(1, report.Totals.Skipped);
    }

    [Fact]
    public void An_open_PM_past_its_grace_is_overdue_and_inside_it_is_not()
    {
        var due = new DateOnly(2026, 9, 17); // three days before "today"

        var strict = Classify(Pm(due, PmTaskStatus.Overdue, grace: 0));
        Assert.Equal(PmOutcome.Overdue, strict.Outcome);
        Assert.Equal(3, strict.DaysLate);

        Assert.Equal(PmOutcome.WithinGrace, Classify(Pm(due, PmTaskStatus.Due, grace: 3)).Outcome);
        Assert.Equal(PmOutcome.Overdue, Classify(Pm(due, PmTaskStatus.Overdue, grace: 2)).Outcome);
    }

    // --- Who is in the count --------------------------------------------------

    [Fact]
    public void A_PM_that_has_not_fallen_due_is_left_out_and_said_so()
    {
        var report = Build(
        [
            Pm(new DateOnly(2026, 9, 10), performedUtc: Ist_(new DateOnly(2026, 9, 10), 9)),
            Pm(new DateOnly(2026, 9, 25), PmTaskStatus.Scheduled),
            Pm(new DateOnly(2026, 9, 28), PmTaskStatus.Scheduled),
        ]);

        Assert.Equal(1, report.Totals.Due);
        Assert.Equal(2, report.NotYetDue);
        Assert.Equal(100.0, report.Totals.OnSchedulePercent);
        Assert.Equal(Today, report.CountedThrough);
    }

    [Fact]
    public void A_period_that_has_ended_counts_through_its_own_last_day()
    {
        var report = Build([Pm(new DateOnly(2026, 9, 10), PmTaskStatus.Overdue)], to: new DateOnly(2026, 9, 15));

        Assert.Equal(new DateOnly(2026, 9, 15), report.CountedThrough);
    }

    [Fact]
    public void Nothing_falling_due_gives_no_percentage_rather_than_zero_or_a_hundred()
    {
        var report = Build([]);

        Assert.Equal(0, report.Totals.Due);
        Assert.Null(report.Totals.OnSchedulePercent);
        Assert.Null(report.Totals.CompletionPercent);
    }

    // --- The figures ------------------------------------------------------------

    [Fact]
    public void On_schedule_and_completion_are_different_figures_and_late_is_the_difference()
    {
        var d = new DateOnly(2026, 9, 3);
        var report = Build(
        [
            Pm(d, performedUtc: Ist_(d, 9), tag: "A"),
            Pm(d, performedUtc: Ist_(d, 9), tag: "B"),
            Pm(d, performedUtc: Ist_(d.AddDays(5), 9), tag: "C"),
            Pm(d, PmTaskStatus.Overdue, tag: "D"),
        ]);

        Assert.Equal(4, report.Totals.Due);
        Assert.Equal(2, report.Totals.OnTime);
        Assert.Equal(1, report.Totals.Late);
        Assert.Equal(1, report.Totals.Overdue);
        Assert.Equal(50.0, report.Totals.OnSchedulePercent);
        Assert.Equal(75.0, report.Totals.CompletionPercent);
    }

    [Fact]
    public void Percentages_are_rounded_to_one_decimal_place()
    {
        var d = new DateOnly(2026, 9, 3);
        var report = Build(
        [
            Pm(d, performedUtc: Ist_(d, 9), tag: "A"),
            Pm(d, performedUtc: Ist_(d, 9), tag: "B"),
            Pm(d, PmTaskStatus.Overdue, tag: "C"),
        ]);

        Assert.Equal(66.7, report.Totals.OnSchedulePercent);
    }

    [Fact]
    public void Findings_are_counted_on_completed_PMs_only()
    {
        var d = new DateOnly(2026, 9, 3);
        var report = Build(
        [
            Pm(d, performedUtc: Ist_(d, 9), tag: "A", outOfRange: 1),
            Pm(d, performedUtc: Ist_(d, 9), tag: "B", failed: 2),
            Pm(d, performedUtc: Ist_(d, 9), tag: "C"),
        ]);

        Assert.Equal(2, report.Totals.WithFindings);
        Assert.Equal(["A", "B"], report.Findings.Select(l => l.Task.AssetTag).ToArray());
    }

    [Fact]
    public void Exceptions_are_the_late_overdue_and_skipped_in_due_date_order()
    {
        var report = Build(
        [
            Pm(new DateOnly(2026, 9, 9), performedUtc: Ist_(new DateOnly(2026, 9, 9), 9), tag: "OK"),
            Pm(new DateOnly(2026, 9, 8), PmTaskStatus.Overdue, tag: "OVER"),
            Pm(new DateOnly(2026, 9, 2), PmTaskStatus.Skipped, tag: "SKIP", skipReason: "Away"),
            Pm(new DateOnly(2026, 9, 5), performedUtc: Ist_(new DateOnly(2026, 9, 12), 9), tag: "LATE"),
            Pm(new DateOnly(2026, 9, 19), PmTaskStatus.Due, grace: 3, tag: "GRACE"),
        ]);

        Assert.Equal(["SKIP", "LATE", "OVER"], report.Exceptions.Select(l => l.Task.AssetTag).ToArray());
    }

    [Fact]
    public void Departments_are_listed_worst_first()
    {
        var d = new DateOnly(2026, 9, 3);
        var report = Build(
        [
            Pm(d, performedUtc: Ist_(d, 9), tag: "A", department: "Radiology"),
            Pm(d, PmTaskStatus.Overdue, tag: "B", department: "ICU"),
            Pm(d, performedUtc: Ist_(d, 9), tag: "C", department: "ICU"),
        ]);

        Assert.Equal(["ICU", "Radiology"], report.ByDepartment.Select(g => g.Name).ToArray());
        Assert.Equal(50.0, report.ByDepartment[0].Totals.OnSchedulePercent);
        Assert.Equal(100.0, report.ByDepartment[1].Totals.OnSchedulePercent);
    }

    // --- The CSV ----------------------------------------------------------------

    [Fact]
    public void The_csv_has_a_row_per_PM_that_fell_due_and_a_header()
    {
        var d = new DateOnly(2026, 9, 3);
        var csv = PmCompliance.ToCsv(Build(
        [
            Pm(d, performedUtc: Ist_(d, 9), tag: "A"),
            Pm(d, PmTaskStatus.Overdue, tag: "B"),
            Pm(new DateOnly(2026, 9, 28), PmTaskStatus.Scheduled, tag: "FUTURE"),
        ]));

        var lines = csv.TrimEnd().Split("\r\n");

        Assert.StartsWith("﻿Asset tag,Equipment type", lines[0], StringComparison.Ordinal);
        Assert.Equal(3, lines.Length); // header + the two that fell due
        Assert.DoesNotContain("FUTURE", csv, StringComparison.Ordinal);
        Assert.Contains("Overdue, not done", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void A_comma_or_quote_in_a_cell_does_not_break_the_row()
    {
        var csv = PmCompliance.ToCsv(Build(
        [
            Pm(new DateOnly(2026, 9, 3), PmTaskStatus.Skipped, skipReason: "Away, \"on loan\" to Ward 4"),
        ]));

        Assert.Contains("\"Away, \"\"on loan\"\" to Ward 4\"", csv, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=SUM(A1)")]
    [InlineData("+91 98220 00000")]
    [InlineData("-2+3")]
    [InlineData("@cmd")]
    public void A_typed_value_that_a_spreadsheet_would_run_as_a_formula_stays_text(string reason)
    {
        var csv = PmCompliance.ToCsv(Build(
        [
            Pm(new DateOnly(2026, 9, 3), PmTaskStatus.Skipped, skipReason: reason),
        ]));

        Assert.Contains("'" + reason, csv, StringComparison.Ordinal);
    }
}
