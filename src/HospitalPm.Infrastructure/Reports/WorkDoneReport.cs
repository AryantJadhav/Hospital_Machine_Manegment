using System.Globalization;
using System.Text;
using HospitalPm.Infrastructure.Export;

namespace HospitalPm.Infrastructure.Reports;

/// <summary>A spare part drawn against a work order. A null cost is one nobody wrote down, not a free part.</summary>
public sealed record WorkDonePart(string PartNumber, string Name, int Quantity, decimal? UnitCost);

/// <summary>One fixed fault, as the endpoint reads it.</summary>
public sealed record WorkDoneInput(
    int WorkOrderId,
    string Number,
    int EquipmentId,
    string AssetTag,
    string EquipmentType,
    string Location,
    string Priority,
    string Fault,
    string? Solution,
    int? DoneByUserId,
    string? DoneBy,
    DateTime ReportedAtUtc,
    DateTime ResolvedAtUtc,
    DateTime? OutOfServiceAtUtc,
    DateTime? BackInServiceAtUtc,
    IReadOnlyList<WorkDonePart> Parts);

public sealed record WorkDoneItem(
    int WorkOrderId,
    string Number,
    int EquipmentId,
    string AssetTag,
    string EquipmentType,
    string Location,
    string Priority,
    string Fault,
    string? Solution,
    string DoneBy,
    DateTime ReportedAtUtc,
    DateTime ResolvedAtUtc,
    double HoursToFix,
    double? DowntimeHours,
    IReadOnlyList<WorkDonePart> Parts,
    decimal PartsCost,
    bool PartsCostMissing);

public sealed record WorkDonePerson(int? UserId, string Name, int Done, double AverageHoursToFix, decimal PartsCost);

public sealed record WorkDoneReportData(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<WorkDoneItem> Items,
    IReadOnlyList<WorkDonePerson> People)
{
    public int Done => Items.Count;

    public decimal PartsCost => Items.Sum(i => i.PartsCost);

    public double AverageHoursToFix => Items.Count == 0 ? 0 : Math.Round(Items.Average(i => i.HoursToFix), 2);
}

/// <summary>
/// The work finished in a period: which faults were fixed, what was done, who did it, how long it
/// took and what parts it used.
///
/// Built from plain values with no database, so the grouping and the sums can be tested directly.
/// </summary>
public static class WorkDoneReport
{
    /// <summary>What the report says for a fault whose resolver was not recorded.</summary>
    public const string NotRecorded = "Not recorded";

    public static WorkDoneReportData Build(IEnumerable<WorkDoneInput> inputs, DateOnly from, DateOnly to)
    {
        var items = inputs
            .Select(i =>
            {
                var priced = i.Parts.Where(p => p.UnitCost is not null).Sum(p => p.UnitCost!.Value * p.Quantity);
                var down = i.OutOfServiceAtUtc is { } o && i.BackInServiceAtUtc is { } b && b >= o
                    ? Math.Round((b - o).TotalHours, 2)
                    : (double?)null;

                return new
                {
                    Item = new WorkDoneItem(
                        i.WorkOrderId,
                        i.Number,
                        i.EquipmentId,
                        i.AssetTag,
                        i.EquipmentType,
                        i.Location,
                        i.Priority,
                        i.Fault,
                        i.Solution,
                        string.IsNullOrWhiteSpace(i.DoneBy) ? NotRecorded : i.DoneBy!,
                        i.ReportedAtUtc,
                        i.ResolvedAtUtc,
                        // Reported to resolved, never negative: a clock set wrong must not turn into a fault
                        // that was fixed before it happened.
                        Math.Round(Math.Max(0, (i.ResolvedAtUtc - i.ReportedAtUtc).TotalHours), 2),
                        down,
                        i.Parts,
                        priced,
                        i.Parts.Any(p => p.UnitCost is null)),
                    i.DoneByUserId,
                };
            })
            .OrderByDescending(x => x.Item.ResolvedAtUtc)
            .ThenBy(x => x.Item.Number, StringComparer.Ordinal)
            .ToList();

        var people = items
            .GroupBy(x => x.Item.DoneBy, StringComparer.OrdinalIgnoreCase)
            .Select(g => new WorkDonePerson(
                g.Select(x => x.DoneByUserId).FirstOrDefault(),
                g.Key,
                g.Count(),
                Math.Round(g.Average(x => x.Item.HoursToFix), 2),
                g.Sum(x => x.Item.PartsCost)))
            // The most work first; the people with nobody recorded last, whatever their count.
            .OrderBy(p => p.Name == NotRecorded ? 1 : 0)
            .ThenByDescending(p => p.Done)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new WorkDoneReportData(from, to, items.Select(x => x.Item).ToList(), people);
    }

    public static string ToCsv(WorkDoneReportData report)
    {
        var sb = new StringBuilder();
        sb.Append('﻿'); // so Excel reads names in UTF-8
        sb.Append(
            "Service request,Asset tag,Equipment type,Location,Priority,Fault,What was done,Done by,Reported,Resolved," +
            "Hours to fix,Machine down (hours),Parts used,Parts cost\r\n");

        foreach (var i in report.Items)
        {
            sb.Append(Csv.Line(
            [
                i.Number,
                i.AssetTag,
                i.EquipmentType,
                i.Location,
                i.Priority,
                i.Fault,
                i.Solution ?? string.Empty,
                i.DoneBy,
                Stamp(i.ReportedAtUtc),
                Stamp(i.ResolvedAtUtc),
                i.HoursToFix.ToString("0.##", CultureInfo.InvariantCulture),
                i.DowntimeHours?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
                string.Join("; ", i.Parts.Select(p => $"{p.Quantity}x {p.PartNumber} {p.Name}")),
                i.PartsCost.ToString("0.00", CultureInfo.InvariantCulture),
            ])).Append("\r\n");
        }

        return sb.ToString();
    }

    // UTC, said as UTC: the file is read in a spreadsheet by someone who cannot ask what zone it is.
    private static string Stamp(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
