using System.Globalization;
using System.Text;
using HospitalPm.Infrastructure.Export;

namespace HospitalPm.Infrastructure.Reports;

/// <summary>One insurance policy a machine has had: the current one, or one that was renewed.</summary>
public sealed record CostPolicyLine(
    string Provider,
    string? PolicyNumber,
    DateOnly ExpiryDate,
    decimal? Cost,
    bool IsCurrent);

/// <summary>
/// One spare part used on a machine: how many, and what they cost. <see cref="CostMissing"/> is set
/// when some of the uses had no cost recorded, so <see cref="Cost"/> is less than the truth.
/// </summary>
public sealed record CostPartLine(string PartNumber, string Name, int Quantity, decimal Cost, bool CostMissing);

/// <summary>What one machine has cost, as the endpoint reads it. A null cost is one nobody recorded.</summary>
public sealed record CostInput(
    int EquipmentId,
    string AssetTag,
    string EquipmentType,
    string Location,
    decimal? PurchaseCost,
    decimal? ContractCost,
    IReadOnlyList<CostPolicyLine> Policies,
    IReadOnlyList<CostPartLine> Parts,
    int PartsWithoutCost);

public sealed record CostMachine(
    int EquipmentId,
    string AssetTag,
    string EquipmentType,
    string Location,
    decimal? PurchaseCost,
    decimal? InsuranceCost,
    decimal? ContractCost,
    decimal PartsCost,
    decimal Total,
    IReadOnlyList<CostPolicyLine> Policies,
    IReadOnlyList<CostPartLine> Parts);

public sealed record CostReportData(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<CostMachine> Machines,
    int MachinesWithoutCost,
    int PartsWithoutCost)
{
    public decimal PurchaseTotal => Machines.Sum(m => m.PurchaseCost ?? 0m);

    public decimal InsuranceTotal => Machines.Sum(m => m.InsuranceCost ?? 0m);

    public decimal ContractTotal => Machines.Sum(m => m.ContractCost ?? 0m);

    public decimal PartsTotal => Machines.Sum(m => m.PartsCost);

    public decimal GrandTotal => Machines.Sum(m => m.Total);
}

/// <summary>
/// What has been spent on each machine and on what: its purchase, every insurance policy it has
/// had, its maintenance contract, and the spare parts used to repair it.
///
/// Purchase, insurance and contract costs are single figures on a machine's record with no date
/// they were spent on, so they are shown as recorded whatever the period. Spare parts are the one
/// cost with a date, so a period narrows only those. The same builder makes a machine's own
/// lifetime figures, so its page and this report cannot disagree.
/// </summary>
public static class CostReport
{
    public static CostReportData Build(IEnumerable<CostInput> inputs, DateOnly from, DateOnly to)
    {
        var machines = new List<CostMachine>();
        var withoutCost = 0;
        var partsWithoutCost = 0;

        foreach (var i in inputs)
        {
            partsWithoutCost += i.PartsWithoutCost;

            var machine = Machine(i);
            if (machine.Total <= 0)
            {
                withoutCost++;
                continue;
            }

            machines.Add(machine);
        }

        return new CostReportData(
            from,
            to,
            machines
                .OrderByDescending(m => m.Total)
                .ThenBy(m => m.AssetTag, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            withoutCost,
            partsWithoutCost);
    }

    /// <summary>One machine's costs, whether or not it has any.</summary>
    public static CostMachine Machine(CostInput i)
    {
        // Null when no policy has a cost on record, so "not recorded" is not shown as a zero.
        var priced = i.Policies.Where(p => p.Cost is not null).ToList();
        decimal? insurance = priced.Count == 0 ? null : priced.Sum(p => p.Cost!.Value);
        var parts = i.Parts.Sum(p => p.Cost);

        return new CostMachine(
            i.EquipmentId,
            i.AssetTag,
            i.EquipmentType,
            i.Location,
            i.PurchaseCost,
            insurance,
            i.ContractCost,
            parts,
            (i.PurchaseCost ?? 0m) + (insurance ?? 0m) + (i.ContractCost ?? 0m) + parts,
            i.Policies,
            i.Parts);
    }

    public static string ToCsv(CostReportData report)
    {
        var sb = new StringBuilder();
        sb.Append('﻿'); // so Excel reads names in UTF-8
        sb.Append("Asset tag,Equipment type,Location,Purchase cost,Insurance cost,Insurance policies,")
          .Append("Maintenance contract cost,Spare parts cost,Spare parts used,Total\r\n");

        foreach (var m in report.Machines)
        {
            sb.Append(Csv.Line(
            [
                m.AssetTag,
                m.EquipmentType,
                m.Location,
                Money(m.PurchaseCost),
                Money(m.InsuranceCost),
                m.Policies.Count == 0 ? string.Empty : m.Policies.Count.ToString(CultureInfo.InvariantCulture),
                Money(m.ContractCost),
                Money(m.PartsCost),
                string.Join("; ", m.Parts.Select(p => $"{p.PartNumber} x{p.Quantity}")),
                Money(m.Total),
            ])).Append("\r\n");
        }

        return sb.ToString();
    }

    private static string Money(decimal? amount)
        => amount?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty;
}
