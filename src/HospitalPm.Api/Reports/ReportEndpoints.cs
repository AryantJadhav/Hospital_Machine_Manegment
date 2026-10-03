using HospitalPm.Api.Auth;
using System.Security.Claims;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Domain.Operations;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Operations;
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

        // Anyone who works on the thing can print it: a technician handing a machine back to a
        // ward is the person who needs the paperwork in their hand.
        group.MapGet("/pm/{taskId:int}/certificate.pdf", CertificateAsync)
            .RequirePermission(Permissions.PmWork);
        group.MapGet("/work-orders/{id:int}/report.pdf", ServiceReportAsync)
            .RequirePermission(Permissions.WorkOrdersView);

        app.MapGet("/api/dashboard", DashboardAsync).WithTags("Dashboard").RequirePermission(Permissions.RegisterView);
    }

    private static async Task<IResult> CertificateAsync(
        int taskId,
        HospitalPmDbContext db,
        IOptions<ReportOptions> options,
        HospitalPm.Infrastructure.Maintenance.HospitalClock clock,
        CancellationToken ct)
    {
        var completion = await db.PmCompletions.AsNoTracking()
            .SingleOrDefaultAsync(c => c.PmTaskId == taskId, ct);

        if (completion is null)
        {
            return Results.NotFound(new { error = "This PM has not been completed." });
        }

        if (completion.PerformedBy == HospitalPm.Domain.Maintenance.PmPerformedBy.Vendor
            || completion.ChecklistTemplateVersionId is null)
        {
            // There are no checklist answers or drawn signature to put on it: either the vendor's
            // own report is the record, or the PM had no checklist and was recorded as done.
            return Results.Conflict(new
            {
                error = "This PM has no checklist, so it has no certificate. Open the PM to see who did it and its report.",
            });
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
                    Expected(item),
                    answer?.IsFailedCheck() ?? false));
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

        var pdf = new PmCertificateDocument(data, options.Value, clock.Offset).GeneratePdf();

        return Results.File(pdf, "application/pdf", $"PM-{task.AssetTag}-{task.DueDate:yyyyMMdd}.pdf");
    }

    private static async Task<IResult> ServiceReportAsync(
        int id,
        HospitalPmDbContext db,
        IOptions<ReportOptions> options,
        HospitalPm.Infrastructure.Maintenance.HospitalClock clock,
        CancellationToken ct)
    {
        // A person in another department is given the report for their own requests, without the money:
        // which parts were used is printed, what they cost is not.
        var showCosts = !db.IsScoped;

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

        var partsUsed = await db.WorkOrderParts.AsNoTracking()
            .Where(p => p.WorkOrderId == id)
            .OrderBy(p => p.UsedAtUtc)
            .Select(p => new ServiceReportPart(
                p.SparePart!.PartNumber, p.SparePart.Name, p.QuantityUsed, showCosts ? p.UnitCostAtUse : null))
            .ToListAsync(ct);

        var photos = await db.WorkOrderAttachments.AsNoTracking()
            .Where(a => a.WorkOrderId == id)
            .OrderBy(a => a.UploadedAtUtc)
            .Select(a => new ServiceReportPhoto(a.FileName, a.Data!.Data))
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
            notes.Select(n => (n.CreatedAtUtc, Name(names, n.AuthorUserId), n.Body)).ToList(),
            partsUsed,
            photos,
            showCosts);

        var pdf = new ServiceReportDocument(data, options.Value, clock.Offset).GeneratePdf();

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
        ClaimsPrincipal user,
        PermissionService permissions,
        CancellationToken ct)
    {
        var today = clock.Today();
        var weekEnd = today.AddDays(7);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var myId = int.TryParse(
            user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                ?? user.FindFirstValue(ClaimTypes.NameIdentifier),
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) ? parsed : 0;

        // The month is the hospital's, the timestamps are UTC. Comparing the
        // stored year and month directly put a PM signed just after midnight
        // on the 1st, India time, into the month before.
        var monthStartUtc = monthStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - clock.Offset;
        var nextMonthStartUtc = monthStart.AddMonths(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - clock.Offset;

        var completedThisMonth = await db.PmTasks.CountAsync(
            t => t.Status == PmTaskStatus.Completed
                 && t.CompletedAtUtc != null
                 && t.CompletedAtUtc >= monthStartUtc
                 && t.CompletedAtUtc < nextMonthStartUtc, ct);

        // Compliance compares like with like: of the PMs that actually fell due
        // this month up to today, how many have been done.
        //
        // It used to divide completedThisMonth — every completion recorded this
        // month, including a year of backlog being cleared — by only the PMs
        // due this month. Two different populations. A department catching up
        // showed either nothing at all (418 completions over a denominator of
        // zero) or a figure well over 100%, which is worse: it is the number an
        // auditor is handed.
        var dueThisMonth = await db.PmTasks.CountAsync(
            t => t.DueDate >= monthStart && t.DueDate <= today, ct);

        var dueThisMonthDone = await db.PmTasks.CountAsync(
            t => t.DueDate >= monthStart && t.DueDate <= today
                 && t.Status == PmTaskStatus.Completed, ct);

        // Admin only, like the Backups page it points at. An employee cannot act
        // on it, and a red tile they cannot fix is just noise on their morning.
        var backup = await permissions.CanAsync(user, Permissions.SystemBackups, ct) ? await BackupStatusAsync(db, clock, ct) : null;

        return Results.Ok(new
        {
            backup,
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
                    : (int)Math.Round(100.0 * dueThisMonthDone / dueThisMonth),
            },
            workOrders = new
            {
                // A fault that has been fixed is no longer open work, so it is not counted here and is
                // not in the work orders list's default view either.
                open = await db.WorkOrders.CountAsync(
                    w => w.Status != WorkOrderStatus.Resolved
                         && w.Status != WorkOrderStatus.Closed
                         && w.Status != WorkOrderStatus.Cancelled, ct),
                critical = await db.WorkOrders.CountAsync(
                    w => w.Priority == WorkOrderPriority.Critical
                         && w.Status != WorkOrderStatus.Resolved
                         && w.Status != WorkOrderStatus.Closed
                         && w.Status != WorkOrderStatus.Cancelled, ct),
                unassigned = await db.WorkOrders.CountAsync(w => w.Status == WorkOrderStatus.Reported, ct),
                // Work waiting on the person looking at the page.
                mine = await db.WorkOrders.CountAsync(
                    w => w.AssignedToUserId == myId
                         && HospitalPm.Api.WorkOrders.WorkOrderEndpoints.OnTheAssigneesPlate.Contains(w.Status), ct),
                machinesDown = await db.WorkOrders.CountAsync(
                    w => w.OutOfServiceAtUtc != null && w.BackInServiceAtUtc == null, ct),
            },
        });
    }

    /// <summary>
    /// The state of the last backup, in the same terms as the Diagnostics page:
    /// none succeeded, the last good one is too old, or the newest attempt
    /// failed while a good one is still recent.
    /// </summary>
    private static async Task<object> BackupStatusAsync(
        HospitalPmDbContext db,
        HospitalPm.Infrastructure.Maintenance.HospitalClock clock,
        CancellationToken ct)
    {
        var last = await db.BackupRuns.AsNoTracking()
            .OrderByDescending(r => r.StartedAtUtc)
            .Select(r => r.Status)
            .Cast<BackupStatus?>()
            .FirstOrDefaultAsync(ct);

        var lastSuccess = await db.BackupRuns.AsNoTracking()
            .Where(r => r.Status == BackupStatus.Succeeded)
            .OrderByDescending(r => r.StartedAtUtc)
            .Select(r => (DateTime?)r.StartedAtUtc)
            .FirstOrDefaultAsync(ct);

        // When this installation began: its oldest account. A fresh install has
        // no backup yet and should not be told it is failing.
        var installedAt = await db.Users.AsNoTracking()
            .MinAsync(u => (DateTime?)u.CreatedAtUtc, ct);

        var state = BackupHealth.Evaluate(last, lastSuccess, installedAt, clock.UtcNow()) switch
        {
            BackupHealthState.Problem => "problem",
            BackupHealthState.Warning => "warn",
            BackupHealthState.Pending => "pending",
            _ => "ok",
        };

        return new { state, lastSuccessUtc = lastSuccess };
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
