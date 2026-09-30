using System.Globalization;
using System.Text;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Export;

namespace HospitalPm.Infrastructure.Reports;

/// <summary>One machine and every stretch it was reported down, as the endpoint reads them.</summary>
public sealed record DowntimeInput(
    int EquipmentId,
    string AssetTag,
    string EquipmentType,
    string Location,
    DateTime RegisteredAtUtc,
    IReadOnlyList<DowntimeWindow> Windows);

public sealed record DowntimeMachine(
    int EquipmentId,
    string AssetTag,
    string EquipmentType,
    string Location,
    int Incidents,
    double DowntimeHours,
    double AvailabilityPercent,
    bool StillDown);

public sealed record DowntimeReportData(DateOnly From, DateOnly To, IReadOnlyList<DowntimeMachine> Machines)
{
    public int Incidents => Machines.Sum(m => m.Incidents);

    public double TotalDowntimeHours => Math.Round(Machines.Sum(m => m.DowntimeHours), 2);

    public int StillDown => Machines.Count(m => m.StillDown);
}

/// <summary>
/// Which machines were down in a period, for how long, and how often.
///
/// Built from plain values with no database, so the period edges can be tested directly.
/// </summary>
public static class DowntimeReport
{
    /// <param name="periodEndUtc">
    /// Where counting stops: the end of the period, or now when the period has not finished.
    /// </param>
    public static DowntimeReportData Build(
        IEnumerable<DowntimeInput> inputs,
        DateOnly from,
        DateOnly to,
        DateTime periodStartUtc,
        DateTime periodEndUtc)
    {
        var machines = new List<DowntimeMachine>();

        foreach (var m in inputs)
        {
            // A machine added mid-period has only been on the register since then, so it has
            // not had the whole period to be up.
            var trackedStart = m.RegisteredAtUtc > periodStartUtc ? m.RegisteredAtUtc : periodStartUtc;
            if (periodEndUtc <= trackedStart)
            {
                continue;
            }

            var overlapping = m.Windows
                .Where(w => w.FromUtc < periodEndUtc && (w.ToUtc is null || w.ToUtc > trackedStart))
                .ToList();

            var trackedMinutes = (periodEndUtc - trackedStart).TotalMinutes;
            var downMinutes = Math.Min(trackedMinutes, Downtime.Minutes(overlapping, periodEndUtc, trackedStart));

            if (downMinutes <= 0)
            {
                continue;
            }

            machines.Add(new DowntimeMachine(
                m.EquipmentId,
                m.AssetTag,
                m.EquipmentType,
                m.Location,
                overlapping.Count,
                Downtime.Hours(downMinutes),
                Math.Round(100.0 * (trackedMinutes - downMinutes) / trackedMinutes, 1),
                overlapping.Any(w => w.ToUtc is null)));
        }

        return new DowntimeReportData(
            from,
            to,
            machines
                .OrderByDescending(m => m.DowntimeHours)
                .ThenBy(m => m.AssetTag, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    public static string ToCsv(DowntimeReportData report)
    {
        var sb = new StringBuilder();
        sb.Append('﻿'); // so Excel reads names in UTF-8
        sb.Append("Asset tag,Equipment type,Location,Incidents,Downtime (hours),Availability (%),Still down\r\n");

        foreach (var m in report.Machines)
        {
            sb.Append(Csv.Line(
            [
                m.AssetTag,
                m.EquipmentType,
                m.Location,
                m.Incidents.ToString(CultureInfo.InvariantCulture),
                m.DowntimeHours.ToString("0.##", CultureInfo.InvariantCulture),
                m.AvailabilityPercent.ToString("0.0", CultureInfo.InvariantCulture),
                m.StillDown ? "Yes" : "No",
            ])).Append("\r\n");
        }

        return sb.ToString();
    }
}
