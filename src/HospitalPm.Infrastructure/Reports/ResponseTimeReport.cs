using System.Globalization;
using System.Text;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Export;

namespace HospitalPm.Infrastructure.Reports;

/// <summary>One fault raised in the period, as the endpoint reads it.</summary>
public sealed record ResponseInput(
    int WorkOrderId,
    string Number,
    int EquipmentId,
    string AssetTag,
    string EquipmentType,
    string Location,
    WorkOrderPriority Priority,
    WorkOrderStatus Status,
    DateTime ReportedAtUtc,
    DateTime? StartedAtUtc,
    int? ResponderUserId,
    string? Responder);

public sealed record ResponseItem(
    int WorkOrderId,
    string Number,
    int EquipmentId,
    string AssetTag,
    string EquipmentType,
    string Location,
    string Priority,
    DateTime ReportedAtUtc,
    DateTime? StartedAtUtc,
    string? Responder,
    int? ResponseMinutes,
    int? TargetMinutes,
    int? OverdueMinutes,
    ResponseState State);

/// <summary>How the faults of one priority were answered against the time that priority allows.</summary>
public sealed record ResponseByPriority(
    string Priority,
    int? TargetMinutes,
    int Raised,
    int OnTime,
    int Late,
    int Waiting,
    int Overdue,
    int? AverageMinutes,
    int? LongestMinutes);

/// <summary>One engineer: how many faults they answered, how many in time, and how fast.</summary>
public sealed record ResponseByPerson(int? UserId, string Name, int Answered, int OnTime, int Late, int? AverageMinutes);

public sealed record ResponseTimeData(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<ResponseItem> Items,
    IReadOnlyList<ResponseByPriority> ByPriority,
    IReadOnlyList<ResponseByPerson> People)
{
    private IEnumerable<ResponseItem> Answered => Items.Where(i => i.ResponseMinutes is not null);

    /// <summary>Faults raised in the period.</summary>
    public int Raised => Items.Count;

    /// <summary>Faults an engineer has started (pressed In progress on), whether or not there was a time to judge them by.</summary>
    public int Responded => Answered.Count();

    public int OnTime => Items.Count(i => i.State == ResponseState.Met);

    public int Late => Items.Count(i => i.State == ResponseState.Missed);

    /// <summary>Not started yet and still within the time allowed.</summary>
    public int Waiting => Items.Count(i => i.State == ResponseState.Waiting);

    /// <summary>Not started yet and already past the time allowed.</summary>
    public int Overdue => Items.Count(i => i.State == ResponseState.Overdue);

    /// <summary>Cancelled, or solved without a recorded start: there is no response to judge.</summary>
    public int NotJudged => Items.Count(i => i.State == ResponseState.NotApplicable && i.ResponseMinutes is null);

    /// <summary>Of the answered faults that had a time to be judged by, the share that was started in time, 0 to 100.</summary>
    public double? OnTimePercent => OnTime + Late == 0 ? null : Math.Round(100.0 * OnTime / (OnTime + Late), 1);

    public int? AverageMinutes => Responded == 0 ? null : ResponseTimeReport.Round(Answered.Average(i => i.ResponseMinutes!.Value));

    public int? MedianMinutes => ResponseTimeReport.Median(Answered.Select(i => i.ResponseMinutes!.Value).ToList());

    public int? LongestMinutes => Responded == 0 ? null : Answered.Max(i => i.ResponseMinutes!.Value);
}

/// <summary>
/// How quickly engineers responded to the faults raised in a period: from the fault being raised to an engineer pressing
/// In progress. Not the time to fix it. Judged against the times the head has set for each priority.
///
/// Built from plain values with no database, so the counting can be tested directly.
/// </summary>
public static class ResponseTimeReport
{
    public const string NotRecorded = "Not recorded";

    /// <summary>Whole minutes, a half going up (12.5 is 13), as a person rounds, not to the even number.</summary>
    public static int Round(double minutes) => (int)Math.Round(minutes, MidpointRounding.AwayFromZero);

    public static ResponseTimeData Build(
        IEnumerable<ResponseInput> inputs,
        IReadOnlyDictionary<WorkOrderPriority, int> targets,
        DateOnly from,
        DateOnly to,
        DateTime nowUtc)
    {
        var items = inputs
            .Select(i =>
            {
                var a = ResponseRules.Assess(
                    i.ReportedAtUtc, i.StartedAtUtc, targets.TryGetValue(i.Priority, out var t) ? t : null, i.Status, nowUtc);

                return new ResponseItem(
                    i.WorkOrderId, i.Number, i.EquipmentId, i.AssetTag, i.EquipmentType, i.Location, i.Priority.ToString(),
                    i.ReportedAtUtc, i.StartedAtUtc,
                    i.StartedAtUtc is null ? null : (string.IsNullOrWhiteSpace(i.Responder) ? NotRecorded : i.Responder),
                    a.ResponseMinutes, a.TargetMinutes, a.OverdueMinutes, a.State);
            })
            // The latest raised first, as the other reports list; the ones still overdue are not hidden by the order.
            .OrderByDescending(i => i.ReportedAtUtc)
            .ThenBy(i => i.Number, StringComparer.Ordinal)
            .ToList();

        var byPriority = Enum.GetValues<WorkOrderPriority>()
            .OrderByDescending(p => p)
            .Select(p =>
            {
                var rows = items.Where(i => i.Priority == p.ToString()).ToList();
                var answered = rows.Where(i => i.ResponseMinutes is not null).Select(i => i.ResponseMinutes!.Value).ToList();
                return new ResponseByPriority(
                    p.ToString(),
                    targets.TryGetValue(p, out var target) ? target : null,
                    rows.Count,
                    rows.Count(i => i.State == ResponseState.Met),
                    rows.Count(i => i.State == ResponseState.Missed),
                    rows.Count(i => i.State == ResponseState.Waiting),
                    rows.Count(i => i.State == ResponseState.Overdue),
                    answered.Count == 0 ? null : ResponseTimeReport.Round(answered.Average()),
                    answered.Count == 0 ? null : answered.Max());
            })
            // A priority with nothing raised is left out, so the table is only what happened.
            .Where(r => r.Raised > 0)
            .ToList();

        var people = inputs
            .Where(i => i.StartedAtUtc is not null)
            .GroupBy(i => string.IsNullOrWhiteSpace(i.Responder) ? NotRecorded : i.Responder!, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var theirs = items.Where(x => g.Any(i => i.WorkOrderId == x.WorkOrderId)).ToList();
                var minutes = theirs.Where(x => x.ResponseMinutes is not null).Select(x => x.ResponseMinutes!.Value).ToList();
                return new ResponseByPerson(
                    g.Select(i => i.ResponderUserId).FirstOrDefault(id => id is not null),
                    g.Key,
                    theirs.Count,
                    theirs.Count(x => x.State == ResponseState.Met),
                    theirs.Count(x => x.State == ResponseState.Missed),
                    minutes.Count == 0 ? null : ResponseTimeReport.Round(minutes.Average()));
            })
            .OrderBy(p => p.Name == NotRecorded ? 1 : 0)
            .ThenByDescending(p => p.Answered)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ResponseTimeData(from, to, items, byPriority, people);
    }

    /// <summary>The middle value, or the average of the two in the middle, rounded. Null for an empty list.</summary>
    public static int? Median(List<int> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : ResponseTimeReport.Round((sorted[mid - 1] + sorted[mid]) / 2.0);
    }

    public static string Result(ResponseItem i) => i.State switch
    {
        ResponseState.Met => "On time",
        ResponseState.Missed => "Late",
        ResponseState.Waiting => "Waiting",
        ResponseState.Overdue => "Overdue",
        _ => "Not judged",
    };

    public static string ToCsv(ResponseTimeData report)
    {
        var sb = new StringBuilder();
        sb.Append('﻿'); // so Excel reads names in UTF-8
        sb.Append(
            "Service request,Asset tag,Equipment type,Location,Priority,Raised,Started (In progress),Started by,Response (minutes)," +
            "Allowed (minutes),Result,Overdue by (minutes)\r\n");

        foreach (var i in report.Items)
        {
            sb.Append(Csv.Line(
            [
                i.Number,
                i.AssetTag,
                i.EquipmentType,
                i.Location,
                i.Priority,
                Stamp(i.ReportedAtUtc),
                i.StartedAtUtc is { } s ? Stamp(s) : string.Empty,
                i.Responder ?? string.Empty,
                i.ResponseMinutes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                i.TargetMinutes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                Result(i),
                i.OverdueMinutes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ])).Append("\r\n");
        }

        return sb.ToString();
    }

    // UTC, said as UTC: the file is read in a spreadsheet by someone who cannot ask what zone it is.
    private static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
