using System.Globalization;
using System.Text;
using HospitalPm.Domain.Inventory;
using HospitalPm.Infrastructure.Export;

namespace HospitalPm.Infrastructure.Reports;

/// <summary>One spare part on the shelf, as the endpoint reads it.</summary>
public sealed record StockInput(
    int Id,
    string PartNumber,
    string Name,
    int QuantityOnHand,
    string Unit,
    string? EquipmentType,
    string? Supplier,
    string? StorageLocation,
    decimal? UnitCost);

public sealed record StockLine(
    int Id,
    string PartNumber,
    string Name,
    int QuantityOnHand,
    string Unit,
    string? EquipmentType,
    string? Supplier,
    string? StorageLocation,
    decimal? UnitCost);

public sealed record StockReportData(IReadOnlyList<StockLine> OutOfStock, IReadOnlyList<StockLine> LowStock);

/// <summary>
/// The spare parts to buy: the ones with none left, and the ones running short (see <see cref="StockRule"/>).
///
/// Built from plain values with no database, so the boundary between the two can be tested directly.
/// </summary>
public static class StockReport
{
    public static StockReportData Build(IEnumerable<StockInput> parts)
    {
        var lines = parts
            .Select(p => (Level: StockRule.For(p.QuantityOnHand), Line: new StockLine(
                p.Id, p.PartNumber, p.Name, p.QuantityOnHand, p.Unit, p.EquipmentType, p.Supplier, p.StorageLocation, p.UnitCost)))
            .ToList();

        return new StockReportData(
            lines.Where(x => x.Level == StockLevel.Out)
                .Select(x => x.Line)
                .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.PartNumber, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            // The fewest left first: the one down to a single piece is the one to order first.
            lines.Where(x => x.Level == StockLevel.Low)
                .Select(x => x.Line)
                .OrderBy(l => l.QuantityOnHand)
                .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.PartNumber, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    public static string ToCsv(StockReportData report)
    {
        var sb = new StringBuilder();
        sb.Append('﻿'); // so Excel reads names in UTF-8
        sb.Append("Status,Part number,Name,In stock,Unit,Used in,Supplier,Where it's kept,Cost per unit\r\n");

        void Rows(string status, IEnumerable<StockLine> lines)
        {
            foreach (var l in lines)
            {
                sb.Append(Csv.Line(
                [
                    status,
                    l.PartNumber,
                    l.Name,
                    l.QuantityOnHand.ToString(CultureInfo.InvariantCulture),
                    l.Unit,
                    l.EquipmentType ?? string.Empty,
                    l.Supplier ?? string.Empty,
                    l.StorageLocation ?? string.Empty,
                    l.UnitCost?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty,
                ])).Append("\r\n");
            }
        }

        Rows("Out of stock", report.OutOfStock);
        Rows("Low stock", report.LowStock);

        return sb.ToString();
    }
}
