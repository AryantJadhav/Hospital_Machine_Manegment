using System.Globalization;
using System.Text;
using HospitalPm.Domain.Maintenance;

namespace HospitalPm.Infrastructure.Reports;

/// <summary>
/// What became of one PM that fell due in the reporting period.
/// </summary>
public enum PmOutcome
{
    /// <summary>Done on or before its due date plus grace.</summary>
    OnTime,

    /// <summary>Done, but after its due date plus grace.</summary>
    Late,

    /// <summary>Deliberately not done, with a reason. Never counted as done.</summary>
    Skipped,

    /// <summary>Past its due date plus grace and still not done.</summary>
    Overdue,

    /// <summary>Due, not done, and still inside its grace period.</summary>
    WithinGrace,
}

/// <summary>One PM that fell due in the period, with what the report needs to say about it.</summary>
public sealed record ComplianceTask(
    int TaskId,
    string AssetTag,
    string EquipmentType,
    string Location,
    string Department,
    string Checklist,
    DateOnly DueDate,
    int GraceDays,
    PmTaskStatus Status,
    DateTime? PerformedAtUtc,
    string? PerformedBy,
    string? SkipReason,
    int OutOfRangeReadings,
    int FailedChecks);

public sealed record ComplianceLine(ComplianceTask Task, PmOutcome Outcome, DateOnly? PerformedOn, int DaysLate)
{
    public bool HasFindings => Task.OutOfRangeReadings + Task.FailedChecks > 0;
}

public sealed record ComplianceTotals(
    int Due,
    int OnTime,
    int Late,
    int Skipped,
    int Overdue,
    int WithinGrace,
    int WithFindings)
{
    public int Completed => OnTime + Late;

    /// <summary>The headline: done by their due date (plus grace), of everything that fell due.</summary>
    public double? OnSchedulePercent => Percent(OnTime, Due);

    /// <summary>Done at all, however late.</summary>
    public double? CompletionPercent => Percent(Completed, Due);

    private static double? Percent(int part, int whole) =>
        whole == 0 ? null : Math.Round(100.0 * part / whole, 1, MidpointRounding.AwayFromZero);
}

public sealed record ComplianceGroup(string Name, ComplianceTotals Totals);

public sealed record ComplianceReport(
    DateOnly From,
    DateOnly To,
    DateOnly CountedThrough,
    string Scope,
    TimeSpan Offset,
    DateTime GeneratedAtUtc,
    string GeneratedBy,
    int NotYetDue,
    int Machines,
    ComplianceTotals Totals,
    IReadOnlyList<ComplianceGroup> ByDepartment,
    IReadOnlyList<ComplianceGroup> ByType,
    IReadOnlyList<ComplianceLine> Lines)
{
    /// <summary>Late, overdue and skipped: what an auditor asks to see explained.</summary>
    public IEnumerable<ComplianceLine> Exceptions => Lines
        .Where(l => l.Outcome is PmOutcome.Late or PmOutcome.Overdue or PmOutcome.Skipped)
        .OrderBy(l => l.Task.DueDate).ThenBy(l => l.Task.AssetTag, StringComparer.OrdinalIgnoreCase);

    /// <summary>Completed PMs that recorded an out-of-range reading or a failed check.</summary>
    public IEnumerable<ComplianceLine> Findings => Lines
        .Where(l => l.HasFindings)
        .OrderBy(l => l.Task.DueDate).ThenBy(l => l.Task.AssetTag, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The arithmetic behind the compliance report, kept free of data access so that
/// what it counts can be tested exactly.
///
/// The definitions are the report's whole value, and they are printed on it:
/// a figure an auditor cannot trace back to a rule is just a number.
///
///  - The population is every PM whose due date falls in the period and has
///    already arrived. A PM due next week is not yet compliant or otherwise.
///  - A completed PM is on time when it was performed by its due date plus the
///    schedule's grace days, on the hospital's calendar, and late after that.
///    When it was performed is the device's time, not when it reached the server.
///  - A skipped PM is not a completed PM. It counts against compliance, and the
///    reason is listed.
/// </summary>
public static class PmCompliance
{
    public static ComplianceReport Build(
        IReadOnlyList<ComplianceTask> tasks,
        DateOnly from,
        DateOnly to,
        DateOnly today,
        TimeSpan offset,
        string scope,
        DateTime generatedAtUtc,
        string generatedBy)
    {
        var countedThrough = to < today ? to : today;
        var lines = new List<ComplianceLine>();
        var notYetDue = 0;

        foreach (var task in tasks)
        {
            if (task.DueDate > countedThrough)
            {
                notYetDue++;
                continue;
            }

            lines.Add(Classify(task, today, offset));
        }

        return new ComplianceReport(
            from,
            to,
            countedThrough,
            scope,
            offset,
            generatedAtUtc,
            generatedBy,
            notYetDue,
            lines.Select(l => l.Task.AssetTag).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            Totals(lines),
            Group(lines, l => l.Task.Department),
            Group(lines, l => l.Task.EquipmentType),
            lines);
    }

    public static ComplianceLine Classify(ComplianceTask task, DateOnly today, TimeSpan offset)
    {
        var lastOnTime = task.DueDate.AddDays(task.GraceDays);

        if (task.Status == PmTaskStatus.Completed && task.PerformedAtUtc is { } performedUtc)
        {
            var performedOn = DateOnly.FromDateTime(performedUtc + offset);
            var late = performedOn > lastOnTime;

            return new ComplianceLine(
                task,
                late ? PmOutcome.Late : PmOutcome.OnTime,
                performedOn,
                Math.Max(0, performedOn.DayNumber - task.DueDate.DayNumber));
        }

        if (task.Status == PmTaskStatus.Skipped)
        {
            return new ComplianceLine(task, PmOutcome.Skipped, null, 0);
        }

        // Not done. Whether that is a failure yet depends on the grace period.
        return today > lastOnTime
            ? new ComplianceLine(task, PmOutcome.Overdue, null, today.DayNumber - task.DueDate.DayNumber)
            : new ComplianceLine(task, PmOutcome.WithinGrace, null, 0);
    }

    private static ComplianceTotals Totals(IEnumerable<ComplianceLine> lines)
    {
        int due = 0, onTime = 0, late = 0, skipped = 0, overdue = 0, grace = 0, findings = 0;

        foreach (var line in lines)
        {
            due++;
            switch (line.Outcome)
            {
                case PmOutcome.OnTime: onTime++; break;
                case PmOutcome.Late: late++; break;
                case PmOutcome.Skipped: skipped++; break;
                case PmOutcome.Overdue: overdue++; break;
                case PmOutcome.WithinGrace: grace++; break;
            }

            if (line.HasFindings)
            {
                findings++;
            }
        }

        return new ComplianceTotals(due, onTime, late, skipped, overdue, grace, findings);
    }

    /// <summary>Worst first, so the departments that need a conversation are at the top.</summary>
    private static IReadOnlyList<ComplianceGroup> Group(
        IEnumerable<ComplianceLine> lines, Func<ComplianceLine, string> key) =>
        lines
            .GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ComplianceGroup(g.Key, Totals(g)))
            .OrderBy(g => g.Totals.OnSchedulePercent ?? 101)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>One row per PM that fell due, for an auditor who wants the working.</summary>
    public static string ToCsv(ComplianceReport report)
    {
        var sb = new StringBuilder();
        sb.Append('﻿'); // so Excel reads names in UTF-8
        sb.Append("Asset tag,Equipment type,Department,Location,Checklist,Due date,Grace days,Outcome,")
          .Append("Performed on,Days late,Performed by,Skip reason,Out-of-range readings,Failed checks\r\n");

        foreach (var l in report.Lines.OrderBy(l => l.Task.DueDate).ThenBy(l => l.Task.AssetTag, StringComparer.OrdinalIgnoreCase))
        {
            var t = l.Task;
            Row(
                sb,
                t.AssetTag,
                t.EquipmentType,
                t.Department,
                t.Location,
                t.Checklist,
                t.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                t.GraceDays.ToString(CultureInfo.InvariantCulture),
                OutcomeLabel(l.Outcome),
                l.PerformedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                l.DaysLate > 0 ? l.DaysLate.ToString(CultureInfo.InvariantCulture) : string.Empty,
                t.PerformedBy ?? string.Empty,
                t.SkipReason ?? string.Empty,
                l.Outcome is PmOutcome.OnTime or PmOutcome.Late
                    ? t.OutOfRangeReadings.ToString(CultureInfo.InvariantCulture) : string.Empty,
                l.Outcome is PmOutcome.OnTime or PmOutcome.Late
                    ? t.FailedChecks.ToString(CultureInfo.InvariantCulture) : string.Empty);
        }

        return sb.ToString();
    }

    public static string OutcomeLabel(PmOutcome outcome) => outcome switch
    {
        PmOutcome.OnTime => "Done on time",
        PmOutcome.Late => "Done late",
        PmOutcome.Skipped => "Skipped",
        PmOutcome.Overdue => "Overdue, not done",
        _ => "Due, within grace",
    };

    private static void Row(StringBuilder sb, params string[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(Cell(cells[i]));
        }

        sb.Append("\r\n");
    }

    /// <summary>
    /// A spreadsheet treats a cell starting with = + - or @ as a formula, and an
    /// asset tag or a skip reason is typed by a person. Prefixing a quote keeps
    /// the text as text.
    /// </summary>
    private static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
