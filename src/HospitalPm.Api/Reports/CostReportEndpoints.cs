using System.Text;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using HospitalPm.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Reports;

/// <summary>
/// What has been spent on each machine, and on what.
///
/// Administrators only: it puts a rupee figure on every machine in the hospital.
/// </summary>
public static class CostReportEndpoints
{
    public static void MapCostReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports/cost")
            .WithTags("Reports")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        group.MapGet("/", SummaryAsync);
        group.MapGet("/report.csv", CsvAsync);
    }

    private static async Task<IResult> SummaryAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int? locationId,
        CancellationToken ct)
    {
        var (scope, report, error) = await BuildAsync(db, clock, from, to, locationId, ct);
        if (error is not null)
        {
            return error;
        }

        return Results.Ok(new
        {
            from = report!.From,
            to = report.To,
            countedThrough = scope!.CountedThrough,
            scope = scope.Scope,
            machines = report.Machines,
            machinesWithoutCost = report.MachinesWithoutCost,
            partsWithoutCost = report.PartsWithoutCost,
            totals = new
            {
                purchase = report.PurchaseTotal,
                insurance = report.InsuranceTotal,
                contract = report.ContractTotal,
                parts = report.PartsTotal,
                total = report.GrandTotal,
            },
        });
    }

    private static async Task<IResult> CsvAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int? locationId,
        CancellationToken ct)
    {
        var (_, report, error) = await BuildAsync(db, clock, from, to, locationId, ct);
        if (error is not null)
        {
            return error;
        }

        return Results.File(
            Encoding.UTF8.GetBytes(CostReport.ToCsv(report!)),
            "text/csv; charset=utf-8",
            $"cost-{report!.From:yyyyMMdd}-{report.To:yyyyMMdd}.csv");
    }

    private static async Task<(ReportScope? Scope, CostReportData? Report, IResult? Error)> BuildAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        DateOnly? from,
        DateOnly? to,
        int? locationId,
        CancellationToken ct)
    {
        var (scope, error) = await ReportScope.ResolveAsync(db, clock, from, to, locationId, ct);
        if (error is not null)
        {
            return (null, null, error);
        }

        var inputs = await CostLoader.LoadAsync(db, null, scope!.PathPrefix, scope.StartUtc, scope.EndUtc, ct);

        return (scope, CostReport.Build(inputs, scope.From, scope.To), null);
    }
}
