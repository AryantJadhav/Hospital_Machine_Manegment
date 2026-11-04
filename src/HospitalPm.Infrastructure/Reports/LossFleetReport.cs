using System.Globalization;
using System.Text;
using HospitalPm.Domain.Losses;
using HospitalPm.Infrastructure.Export;

namespace HospitalPm.Infrastructure.Reports;

/// <summary>One loss as the report reads it: already narrowed to the period and the place, and with none that was written in error.</summary>
public sealed record LossFleetInput(
    int Id,
    DateTime ReportedAtUtc,
    int EquipmentId,
    string AssetTag,
    string? EquipmentType,
    string? Location,
    LossKind Kind,
    LossStatus Status,
    DateOnly NoticedOn,
    DateOnly? LastSeenOn,
    DateOnly? RecoveredOn,
    decimal? Value,
    string? PoliceReportNumber);

public sealed record LossFleetItem(
    int Id,
    string Reference,
    int EquipmentId,
    string AssetTag,
    string? EquipmentType,
    string? Location,
    string Kind,
    string Status,
    string StatusLabel,
    DateOnly NoticedOn,
    DateOnly? LastSeenOn,
    DateOnly? RecoveredOn,
    // How long it has been gone: until today while it is missing, until the day it was found once it is back. Null when written off.
    int? DaysMissing,
    decimal? Value,
    string? PoliceReportNumber);

public sealed record LossFleetCount(string Label, int Count);

public sealed record LossFleetRepeat(int EquipmentId, string AssetTag, string? EquipmentType, int Losses);

/// <summary>The machines lost in a period: how many, how they went, what they were worth and how each case ended.</summary>
public sealed record LossFleetData(
    DateOnly From,
    DateOnly To,
    int Total,
    int Open,
    int Recovered,
    int WrittenOff,
    int WithPoliceComplaint,
    // What the machines were worth: all of them, what is still missing or accepted as lost, and what came back.
    decimal ValueReported,
    decimal ValueLost,
    decimal ValueRecovered,
    // Lost machines whose value nobody recorded, so the figures above are not mistaken for the whole.
    int WithoutValue,
    IReadOnlyList<LossFleetCount> ByKind,
    IReadOnlyList<LossFleetCount> ByLocation,
    IReadOnlyList<LossFleetRepeat> RepeatMachines,
    IReadOnlyList<LossFleetItem> Items);

/// <summary>
/// The fleet report of lost equipment: the same records the Loss of equipment pages keep, read across a period for the
/// hospital's asset review and its insurer. A loss belongs to the day the machine was found missing. One written in error
/// (cancelled) is not a loss and is left out of every figure.
/// </summary>
public static class LossFleetReport
{
    public static LossFleetData Build(IEnumerable<LossFleetInput> inputs, DateOnly from, DateOnly to, DateOnly today)
    {
        var rows = inputs.ToList();

        var items = rows
            // The latest first, as the other reports list; the machine that has been gone longest is not above the rest.
            .OrderByDescending(r => r.NoticedOn)
            .ThenByDescending(r => r.Id)
            .Select(r => new LossFleetItem(
                r.Id,
                EquipmentLoss.ReferenceFor(r.ReportedAtUtc, r.Id),
                r.EquipmentId,
                r.AssetTag,
                r.EquipmentType,
                r.Location,
                LossWords.Kind(r.Kind),
                r.Status.ToString(),
                LossWords.Status(r.Status),
                r.NoticedOn,
                r.LastSeenOn,
                r.RecoveredOn,
                DaysMissing(r, today),
                r.Value,
                r.PoliceReportNumber))
            .ToList();

        var recovered = rows.Where(r => r.Status == LossStatus.Recovered).ToList();
        var notRecovered = rows.Where(r => r.Status != LossStatus.Recovered).ToList();

        return new LossFleetData(
            from,
            to,
            rows.Count,
            rows.Count(r => LossWords.IsOpen(r.Status)),
            recovered.Count,
            rows.Count(r => r.Status == LossStatus.WrittenOff),
            rows.Count(r => !string.IsNullOrWhiteSpace(r.PoliceReportNumber)),
            rows.Sum(r => r.Value ?? 0m),
            notRecovered.Sum(r => r.Value ?? 0m),
            recovered.Sum(r => r.Value ?? 0m),
            rows.Count(r => r.Value is null),
            // All three kinds, in order, including those with none: "nothing was stolen" is worth saying.
            Enum.GetValues<LossKind>().Select(k => new LossFleetCount(LossWords.Kind(k), rows.Count(r => r.Kind == k))).ToList(),
            rows.GroupBy(r => r.Location ?? "Not recorded")
                .Select(g => new LossFleetCount(g.Key, g.Count()))
                .OrderByDescending(c => c.Count).ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
            rows.GroupBy(r => r.EquipmentId).Where(g => g.Count() >= 2)
                .Select(g => new LossFleetRepeat(g.Key, g.First().AssetTag, g.First().EquipmentType, g.Count()))
                .OrderByDescending(m => m.Losses).ThenBy(m => m.AssetTag, StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
            items);
    }

    private static int? DaysMissing(LossFleetInput r, DateOnly today) => r.Status switch
    {
        LossStatus.Reported or LossStatus.Investigating or LossStatus.AwaitingDecision => Math.Max(0, today.DayNumber - r.NoticedOn.DayNumber),
        LossStatus.Recovered when r.RecoveredOn is { } found => Math.Max(0, found.DayNumber - r.NoticedOn.DayNumber),
        _ => null,
    };

    public static string ToCsv(LossFleetData report)
    {
        var sb = new StringBuilder();
        sb.Append('﻿'); // so Excel reads names in UTF-8
        sb.Append("Reference,Machine number,Machine,Meant to be in,How,Found missing,Last seen,Status,Found on,Days missing,Value (Rs),Police complaint\r\n");

        foreach (var i in report.Items)
        {
            sb.Append(Csv.Line(
            [
                i.Reference,
                i.AssetTag,
                i.EquipmentType ?? string.Empty,
                i.Location ?? string.Empty,
                i.Kind,
                i.NoticedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                i.LastSeenOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                i.StatusLabel,
                i.RecoveredOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                i.DaysMissing?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                i.Value?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty,
                i.PoliceReportNumber ?? string.Empty,
            ])).Append("\r\n");
        }

        return sb.ToString();
    }
}
