using System.Text;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Persistence;
using HospitalPm.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Reports;

/// <summary>
/// The spare parts that are out of stock and the ones running low, by name.
///
/// Administrators only, like the other reports: it is the list the department head buys from. It is
/// the shelf as it is now, so there is no period to choose. Retired parts are left out: a part the
/// department no longer stocks is not one to buy.
/// </summary>
public static class StockReportEndpoints
{
    public static void MapStockReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports/stock")
            .WithTags("Reports")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        group.MapGet("/", SummaryAsync);
        group.MapGet("/report.csv", CsvAsync);
    }

    private static async Task<IResult> SummaryAsync(HospitalPmDbContext db, CancellationToken ct)
    {
        var report = await BuildAsync(db, ct);

        return Results.Ok(new
        {
            outOfStockCount = report.OutOfStock.Count,
            lowStockCount = report.LowStock.Count,
            outOfStock = report.OutOfStock,
            lowStock = report.LowStock,
        });
    }

    private static async Task<IResult> CsvAsync(HospitalPmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var report = await BuildAsync(db, ct);

        return Results.File(
            Encoding.UTF8.GetBytes(StockReport.ToCsv(report)),
            "text/csv; charset=utf-8",
            $"spare-part-stock-{clock.GetUtcNow():yyyyMMdd}.csv");
    }

    private static async Task<StockReportData> BuildAsync(HospitalPmDbContext db, CancellationToken ct)
    {
        // Only what can be short is read: the rule is applied again over these in StockReport, so
        // the database filter is just a way of not reading the whole shelf.
        var rows = await db.SpareParts.AsNoTracking()
            .Where(p => p.IsActive && p.QuantityOnHand <= Domain.Inventory.StockRule.LowStockMax)
            .Select(p => new StockInput(
                p.Id,
                p.PartNumber,
                p.Name,
                p.QuantityOnHand,
                p.Unit,
                p.EquipmentType == null ? null : p.EquipmentType.Name,
                p.Supplier,
                p.StorageLocation,
                p.UnitCost))
            .ToListAsync(ct);

        return StockReport.Build(rows);
    }
}
