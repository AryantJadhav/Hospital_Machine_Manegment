using HospitalPm.Domain.Maintenance;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Persistence;
using HospitalPm.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuestPDF.Fluent;

namespace HospitalPm.Api.Reports;

public static class ReportEndpoints
{
    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports").WithTags("Reports").RequireAuthorization();

        // Every role can print: a technician handing a machine back to a
        // ward is the person who needs the paperwork in their hand.
        group.MapGet("/pm/{taskId:int}/certificate.pdf", CertificateAsync);
        group.MapGet("/work-orders/{id:int}/report.pdf", ServiceReportAsync);

        app.MapGet("/api/dashboard", DashboardAsync).WithTags("Dashboard").RequireAuthorization();
    }

    private static async Task<IResult> CertificateAsync(
        int taskId,
        HospitalPmDbContext db,
        IOptions<ReportOptions> options,
        CancellationToken ct)
    {
        var completion = await db.PmCompletions.AsNoTracking()
            .SingleOrDefaultAsync(c => c.PmTaskId == taskId, ct);

        if (completion is null)
        {
            return Results.NotFound(new { error = "This PM has not been completed." });
        }

        var task = await db.PmTasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => new
            {
                t.DueDate,
                t.Equipment!.AssetTag,
                t.Equipment!.SerialNumber,
                t.Equipment!.Manufacturer,
                t.Equipment!.Model,
                EquipmentTypeName = t.Equipment!.EquipmentType!.Name,
                LocationName = t.Equipment!.Location!.Name,
                ChecklistName = t.Schedule!.ChecklistTemplate!.Name,
            })
            .SingleAsync(ct);

        // The version filled under, not the current one. This is the whole
        // reason it was pinned at submission.
        var version = await db.ChecklistTemplateVersions.AsNoTracking()
            .SingleAsync(v => v.Id == completion.ChecklistTemplateVersionId, ct);

        var technician = await db.Users.AsNoTracking()
            .Where(u => u.Id == completion.CompletedByUserId)
            .Select(u => u.FullName)
            .SingleOrDefaultAsync(ct) ?? "Unknown";

        var lines = new List<CertificateLine>();

        foreach (var section in version.Definition.Sections)
        {
            foreach (var item in section.Items)
            {
                completion.Answers.TryGetValue(item.Key, out var answer);

                lines.Add(new CertificateLine(
                    section.Title,
                    item.Label,
                    Present(answer?.Value),
                    answer?.Note,
                    answer?.OutOfRange ?? false,
                    Expected(item)));
            }
        }

        var data = new PmCertificateData(
            task.AssetTag,
            task.EquipmentTypeName,
            task.LocationName,
            task.SerialNumber,
            task.Manufacturer,
            task.Model,
            task.ChecklistName,
            version.VersionNo,
            completion.PerformedAtUtc,
            completion.CompletedAtUtc,
            task.DueDate,
            technician,
            completion.SignedByName,
            completion.Signature,
            completion.SignatureFormat,
            completion.Notes,
            lines);

        var pdf = new PmCertificateDocument(data, options.Value).GeneratePdf();

        return Results.File(pdf, "application/pdf", $"PM-{task.AssetTag}-{task.DueDate:yyyyMMdd}.pdf");
    }

    private static async Task<IResult> ServiceReportAsync(
        int id,
        HospitalPmDbContext db,
        IOptions<ReportOptions> options,
        CancellationToken ct)
    {
        var order = await db.WorkOrders.AsNoTracking()
            .Include(w => w.Equipment)!.ThenInclude(e => e!.EquipmentType)
            .Include(w => w.Equipment)!.ThenInclude(e => e!.Location)
            .SingleOrDefaultAsync(w => w.Id == id, ct);

        if (order is null)
        {
            return Results.NotFound();
        }

        var names = await db.Users.AsNoTracking()
            .Select(u => new { u.Id, u.FullName })
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        var notes = await db.WorkOrderNotes.AsNoTracking()
            .Where(n => n.WorkOrderId == id)
            .OrderBy(n => n.CreatedAtUtc)
            .Select(n => new { n.CreatedAtUtc, n.AuthorUserId, n.Body })
            .ToListAsync(ct);

        var data = new ServiceReportData(
            order.Number,
            order.Equipment?.AssetTag ?? "—",
            order.Equipment?.EquipmentType?.Name ?? "—",
            order.Equipment?.Location?.Name ?? "—",
            order.Equipment?.SerialNumber,
            order.Equipment?.Manufacturer,
            order.Equipment?.Model,
            order.FaultDescription,
            order.Priority.ToString(),
            order.Status.ToString(),
            order.ReportedAtUtc,
            Name(names, order.ReportedByUserId),
            order.StartedAtUtc,
            order.ResolvedAtUtc,
            order.ResolvedByUserId is { } r ? Name(names, r) : null,
            order.ResolutionNotes,
            order.OutOfServiceAtUtc,
            order.BackInServiceAtUtc,
            order.DowntimeMinutes,
            notes.Select(n => (n.CreatedAtUtc, Name(names, n.AuthorUserId), n.Body)).ToList());

        var pdf = new ServiceReportDocument(data, options.Value).GeneratePdf();

        return Results.File(pdf, "application/pdf", $"{order.Number}.pdf");
    }

    /// <summary>
    /// One call for the whole landing page.
    ///
    /// Assembled server-side rather than as six separate requests: a
    /// dashboard that fires a burst of queries at a hospital PC also running
    /// Postgres is how the first screen of the day gets slow.
    /// </summary>
    private static async Task<IResult> DashboardAsync(
        HospitalPmDbContext db,
        HospitalPm.Infrastructure.Maintenance.HospitalClock clock,
        CancellationToken ct)
    {
        var today = clock.Today();
        var weekEnd = today.AddDays(7);
        var monthStart = new DateOnly(today.Year, today.Month, 1);

        var completedThisMonth = await db.PmTasks.CountAsync(
            t => t.Status == PmTaskStatus.Completed
                 && t.CompletedAtUtc != null
                 && t.CompletedAtUtc!.Value.Year == today.Year
                 && t.CompletedAtUtc!.Value.Month == today.Month, ct);

        var dueThisMonth = await db.PmTasks.CountAsync(
            t => t.DueDate >= monthStart && t.DueDate <= today, ct);

        return Results.Ok(new
        {
            equipment = new
            {
                total = await db.Equipment.CountAsync(ct),
                inService = await db.Equipment.CountAsync(
                    e => e.Status == Domain.Assets.EquipmentStatus.InService, ct),
                underRepair = await db.Equipment.CountAsync(
                    e => e.Status == Domain.Assets.EquipmentStatus.UnderRepair, ct),
            },
            pm = new
            {
                overdue = await db.PmTasks.CountAsync(t => t.Status == PmTaskStatus.Overdue, ct),
                dueToday = await db.PmTasks.CountAsync(
                    t => t.Status == PmTaskStatus.Due && t.DueDate == today, ct),
                dueThisWeek = await db.PmTasks.CountAsync(
                    t => (t.Status == PmTaskStatus.Due || t.Status == PmTaskStatus.Scheduled)
                         && t.DueDate >= today && t.DueDate <= weekEnd, ct),
                completedThisMonth,
                // The headline number an auditor asks for. Guarded against a
                // zero denominator, which is every hospital on day one.
                complianceThisMonth = dueThisMonth == 0
                    ? (int?)null
                    : (int)Math.Round(100.0 * completedThisMonth / dueThisMonth),
            },
            workOrders = new
            {
                open = await db.WorkOrders.CountAsync(
                    w => w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Cancelled, ct),
                critical = await db.WorkOrders.CountAsync(
                    w => w.Priority == WorkOrderPriority.Critical
                         && w.Status != WorkOrderStatus.Closed
                         && w.Status != WorkOrderStatus.Cancelled, ct),
                unassigned = await db.WorkOrders.CountAsync(w => w.Status == WorkOrderStatus.Reported, ct),
                machinesDown = await db.WorkOrders.CountAsync(
                    w => w.OutOfServiceAtUtc != null && w.BackInServiceAtUtc == null, ct),
            },
        });
    }

    private static string Name(Dictionary<int, string> names, int id)
        => names.TryGetValue(id, out var name) ? name : "Unknown";

    /// <summary>Turns a stored answer into what a reader expects to see.</summary>
    private static string Present(string? value) => value switch
    {
        null or "" => "Not answered",
        "pass" => "Pass",
        "fail" => "Fail",
        "yes" => "Yes",
        "no" => "No",
        "na" => "N/A",
        _ => value,
    };

    /// <summary>The acceptable range, so a reader can judge the result themselves.</summary>
    private static string? Expected(Domain.Checklists.ChecklistItem item)
    {
        if (item.Type != Domain.Checklists.ChecklistItemType.Number)
        {
            return null;
        }

        var unit = string.IsNullOrWhiteSpace(item.Unit) ? string.Empty : $" {item.Unit}";

        return (item.Min, item.Max) switch
        {
            (null, null) => null,
            (not null, null) => $"≥ {item.Min}{unit}",
            (null, not null) => $"≤ {item.Max}{unit}",
            _ => $"{item.Min}–{item.Max}{unit}",
        };
    }
}
