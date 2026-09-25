using System.Security.Claims;
using System.Text;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using HospitalPm.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuestPDF.Fluent;

namespace HospitalPm.Api.Reports;

/// <summary>
/// The PM compliance report an accreditation assessor asks for.
///
/// Administrators only: it is the biomedical head's deliverable, and it names
/// every late and skipped PM.
/// </summary>
public static class PmComplianceEndpoints
{
    /// <summary>A report for more than ten years is a mistake, not a request.</summary>
    private const int MaxDays = 3660;

    /// <summary>
    /// Completions are read in batches: their answers are the bulk of the data
    /// and the report only needs two counts out of each.
    /// </summary>
    private const int Batch = 2000;

    public static void MapPmComplianceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports/pm-compliance")
            .WithTags("Reports")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        group.MapGet("/", SummaryAsync);
        group.MapGet("/report.pdf", PdfAsync);
        group.MapGet("/report.csv", CsvAsync);
    }

    private static async Task<IResult> SummaryAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal user,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int? locationId,
        CancellationToken ct)
    {
        var built = await BuildAsync(db, clock, user, from, to, locationId, ct);
        if (built.Error is not null)
        {
            return built.Error;
        }

        var r = built.Report!;

        return Results.Ok(new
        {
            from = r.From,
            to = r.To,
            countedThrough = r.CountedThrough,
            scope = r.Scope,
            notYetDue = r.NotYetDue,
            machines = r.Machines,
            totals = Totals(r.Totals),
            byDepartment = r.ByDepartment.Select(g => new { g.Name, totals = Totals(g.Totals) }),
            byType = r.ByType.Select(g => new { g.Name, totals = Totals(g.Totals) }),
            exceptions = r.Exceptions.Count(),
        });
    }

    private static object Totals(ComplianceTotals t) => new
    {
        t.Due,
        t.OnTime,
        t.Late,
        t.Skipped,
        t.Overdue,
        t.WithinGrace,
        t.WithFindings,
        t.Completed,
        onSchedulePercent = t.OnSchedulePercent,
        completionPercent = t.CompletionPercent,
    };

    private static async Task<IResult> PdfAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        IOptions<ReportOptions> options,
        ClaimsPrincipal user,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int? locationId,
        CancellationToken ct)
    {
        var built = await BuildAsync(db, clock, user, from, to, locationId, ct);
        if (built.Error is not null)
        {
            return built.Error;
        }

        var pdf = new PmComplianceDocument(built.Report!, options.Value).GeneratePdf();
        return Results.File(pdf, "application/pdf", FileName(built.Report!, "pdf"));
    }

    private static async Task<IResult> CsvAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal user,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int? locationId,
        CancellationToken ct)
    {
        var built = await BuildAsync(db, clock, user, from, to, locationId, ct);
        if (built.Error is not null)
        {
            return built.Error;
        }

        return Results.File(
            Encoding.UTF8.GetBytes(PmCompliance.ToCsv(built.Report!)),
            "text/csv; charset=utf-8",
            FileName(built.Report!, "csv"));
    }

    private static string FileName(ComplianceReport r, string extension) =>
        $"PM-compliance-{r.From:yyyyMMdd}-{r.To:yyyyMMdd}.{extension}";

    private sealed record Built(ComplianceReport? Report, IResult? Error);

    private static async Task<Built> BuildAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal principal,
        DateOnly? fromParam,
        DateOnly? toParam,
        int? locationId,
        CancellationToken ct)
    {
        var today = clock.Today();

        // Left out, this month so far: the question most often asked on a Monday.
        var from = fromParam ?? new DateOnly(today.Year, today.Month, 1);
        var to = toParam ?? today;

        if (to < from)
        {
            return new Built(null, Results.BadRequest(new { error = "The end of the period is before its start." }));
        }

        if (to.DayNumber - from.DayNumber > MaxDays)
        {
            return new Built(null, Results.BadRequest(new { error = "Choose a period of ten years or less." }));
        }

        var locations = await db.Locations.AsNoTracking()
            .Select(l => new { l.Id, l.Name, l.Level, l.Path })
            .ToListAsync(ct);

        string scope = "Whole hospital";
        string? prefix = null;

        if (locationId is not null)
        {
            var chosen = locations.SingleOrDefault(l => l.Id == locationId);
            if (chosen is null)
            {
                return new Built(null, Results.NotFound(new { error = "Unknown location." }));
            }

            scope = chosen.Name;
            prefix = chosen.Path;
        }

        // The department a machine's room belongs to: the ancestor at
        // department level, else the place itself. A report by room would be
        // hundreds of rows an auditor cannot read.
        var departments = locations.Where(l => l.Level == LocationLevel.Department).ToList();
        var departmentOf = new Dictionary<int, string>();

        string Department(int locationId2, string locationName, string path)
        {
            if (departmentOf.TryGetValue(locationId2, out var cached))
            {
                return cached;
            }

            var owner = departments
                .Where(d => path.StartsWith(d.Path, StringComparison.Ordinal))
                .OrderByDescending(d => d.Path.Length)
                .FirstOrDefault();

            return departmentOf[locationId2] = owner?.Name ?? locationName;
        }

        var query = db.PmTasks.AsNoTracking().Where(t => t.DueDate >= from && t.DueDate <= to);

        if (prefix is not null)
        {
            query = query.Where(t => t.Equipment!.Location!.Path.StartsWith(prefix));
        }

        var rows = await query
            .Select(t => new
            {
                t.Id,
                t.DueDate,
                t.Status,
                t.SkipReason,
                t.CompletedAtUtc,
                Grace = t.Schedule!.GraceDays,
                Tag = t.Equipment!.AssetTag,
                Type = t.Equipment!.EquipmentType!.Name,
                LocationId = t.Equipment!.LocationId,
                Place = t.Equipment!.Location!.Name,
                PlacePath = t.Equipment!.Location!.Path,
                Checklist = t.Schedule!.ChecklistTemplate!.Name ?? "PM",
                Done = db.PmCompletions
                    .Where(c => c.PmTaskId == t.Id)
                    .Select(c => new
                    {
                        When = c.PerformedAtUtc ?? c.CompletedAtUtc,
                        By = c.SignedByName
                             ?? db.Users.Where(u => u.Id == c.CompletedByUserId).Select(u => u.FullName).FirstOrDefault(),
                    })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        // Findings, counted the way the certificate counts them, a batch at a time.
        var findings = new Dictionary<int, (int OutOfRange, int Failed)>();
        var completedIds = rows.Where(r => r.Done is not null).Select(r => r.Id).ToList();

        for (var i = 0; i < completedIds.Count; i += Batch)
        {
            var ids = completedIds.Skip(i).Take(Batch).ToList();
            var answers = await db.PmCompletions.AsNoTracking()
                .Where(c => ids.Contains(c.PmTaskId))
                .Select(c => new { c.PmTaskId, c.Answers })
                .ToListAsync(ct);

            foreach (var a in answers)
            {
                findings[a.PmTaskId] = (
                    a.Answers.Count(x => x.Value.OutOfRange),
                    a.Answers.Count(x => x.Value.IsFailedCheck()));
            }
        }

        var tasks = rows.Select(r =>
        {
            findings.TryGetValue(r.Id, out var f);

            return new ComplianceTask(
                r.Id,
                r.Tag,
                r.Type,
                r.Place,
                Department(r.LocationId, r.Place, r.PlacePath),
                r.Checklist,
                r.DueDate,
                r.Grace,
                r.Status,
                r.Done?.When ?? r.CompletedAtUtc,
                r.Done?.By,
                r.SkipReason,
                f.OutOfRange,
                f.Failed);
        }).ToList();

        var sub = principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                  ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var name = int.TryParse(sub, out var id)
            ? await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.FullName).FirstOrDefaultAsync(ct)
            : null;

        var report = PmCompliance.Build(
            tasks,
            from,
            to,
            today,
            clock.Offset,
            scope,
            clock.UtcNow(),
            name ?? principal.Identity?.Name ?? "Unknown");

        return new Built(report, null);
    }
}
