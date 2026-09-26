using System.Security.Claims;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.WorkOrders;

public sealed record ReportRequest(
    int EquipmentId,
    string FaultDescription,
    WorkOrderPriority Priority);

public sealed record AssignRequest(int? AssignedToUserId, string? Note);

public sealed record ResolveRequest(string ResolutionNotes);

public sealed record StatusRequest(WorkOrderStatus Status, string? Note);

public sealed record NoteRequest(string Body);

public static class WorkOrderEndpoints
{
    private const int MaxPageSize = 200;

    public static void MapWorkOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/work-orders").WithTags("Work orders").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapGet("/summary", SummaryAsync);

        // Anyone who works the floor can report a fault. A ward that cannot
        // raise a ticket phones the biomedical department instead, and the
        // fault never enters the record at all.
        group.MapPost("/", ReportAsync);
        group.MapPost("/{id:int}/notes", AddNoteAsync);
        group.MapPost("/{id:int}/status", ChangeStatusAsync);
        group.MapPost("/{id:int}/resolve", ResolveAsync);

        // Assignment is a supervisory decision about who does the work.
        group.MapPost("/{id:int}/assign", AssignAsync)
            .RequireAuthorization(p => p.RequireRole(
                Roles.Admin));
    }

    /// <summary>
    /// A work order is on its assignee's plate while it is assigned, being
    /// worked, or waiting on a part. Resolved is the engineer's own "done":
    /// what is left is an administrator accepting it, which is not their work
    /// any more. The dashboard's count and the list behind it use the same set,
    /// so the number on the tile is the number of rows they land on.
    /// </summary>
    public static readonly WorkOrderStatus[] OnTheAssigneesPlate =
        [WorkOrderStatus.Assigned, WorkOrderStatus.InProgress, WorkOrderStatus.OnHold];

    private static async Task<IResult> ListAsync(
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        [FromQuery] WorkOrderStatus? status,
        [FromQuery] int? equipmentId,
        [FromQuery] int? assignedToUserId,
        [FromQuery] string? assignee,
        [FromQuery] bool openOnly = true,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.WorkOrders.AsNoTracking();

        // "me" is resolved here, from the token, so the page needs no idea what
        // its user's number is and cannot ask for someone else's by mistake.
        var mine = string.Equals(assignee, "me", StringComparison.OrdinalIgnoreCase);
        if (mine)
        {
            assignedToUserId = UserId(principal);
        }

        if (status is not null)
        {
            query = query.Where(w => w.Status == status);
        }
        else if (mine && openOnly)
        {
            query = query.Where(w => OnTheAssigneesPlate.Contains(w.Status));
        }
        else if (openOnly)
        {
            query = query.Where(w => w.Status != WorkOrderStatus.Closed
                                  && w.Status != WorkOrderStatus.Cancelled);
        }

        if (equipmentId is not null)
        {
            query = query.Where(w => w.EquipmentId == equipmentId);
        }

        if (assignedToUserId is not null)
        {
            query = query.Where(w => w.AssignedToUserId == assignedToUserId);
        }

        var total = await query.CountAsync(ct);

        var items = await query
            // Worst first, then oldest. This is the queue an engineer works
            // down, so a critical fault raised an hour ago must outrank a low
            // one from last week.
            .OrderByDescending(w => w.Priority)
            .ThenBy(w => w.ReportedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(w => new
            {
                w.Id,
                w.Number,
                w.Status,
                w.Priority,
                w.FaultDescription,
                w.EquipmentId,
                w.Equipment!.AssetTag,
                EquipmentTypeName = w.Equipment!.EquipmentType!.Name,
                LocationName = w.Equipment!.Location!.Name,
                w.ReportedAtUtc,
                w.AssignedToUserId,
                AssignedToName = w.AssignedToUserId == null
                    ? null
                    : db.Users.Where(u => u.Id == w.AssignedToUserId).Select(u => u.FullName).FirstOrDefault(),
                w.OutOfServiceAtUtc,
                w.BackInServiceAtUtc,
            })
            .ToListAsync(ct);

        return Results.Ok(new { items, total, page, pageSize });
    }

    private static async Task<IResult> GetAsync(
        int id, HospitalPmDbContext db, ClaimsPrincipal principal, TimeProvider clock, CancellationToken ct)
    {
        var order = await db.WorkOrders.AsNoTracking()
            .Include(w => w.Equipment)!.ThenInclude(e => e!.EquipmentType)
            .Include(w => w.Equipment)!.ThenInclude(e => e!.Location)
            .SingleOrDefaultAsync(w => w.Id == id, ct);

        if (order is null)
        {
            return Results.NotFound();
        }

        var notes = await db.WorkOrderNotes.AsNoTracking()
            .Where(n => n.WorkOrderId == id)
            .OrderBy(n => n.CreatedAtUtc)
            .Select(n => new { n.Id, n.Body, n.StatusAfter, n.AuthorUserId, n.CreatedAtUtc })
            .ToListAsync(ct);

        return Results.Ok(new
        {
            order.Id,
            order.Number,
            order.Status,
            order.Priority,
            order.FaultDescription,
            order.EquipmentId,
            assetTag = order.Equipment?.AssetTag,
            // The list endpoint returns these and the detail panel shows the
            // same header, so omitting them here left it rendering
            // "BME-0001 · ·" with two empty separators.
            equipmentTypeName = order.Equipment?.EquipmentType?.Name,
            locationName = order.Equipment?.Location?.Name,
            order.ReportedByUserId,
            order.ReportedAtUtc,
            order.AssignedToUserId,
            order.AssignedAtUtc,
            order.StartedAtUtc,
            order.ResolutionNotes,
            order.ResolvedAtUtc,
            order.ClosedAtUtc,
            order.OutOfServiceAtUtc,
            order.BackInServiceAtUtc,
            order.DowntimeMinutes,
            // Hours the machine has been down for this report, up to now while it still is. A report
            // that was cancelled was not an outage, so it counts for nothing.
            downtimeHours = order.OutOfServiceAtUtc is { } down && order.Status != WorkOrderStatus.Cancelled
                ? Downtime.Hours(Downtime.Minutes(
                    [new DowntimeWindow(down, order.BackInServiceAtUtc)], clock.GetUtcNow().UtcDateTime))
                : (double?)null,
            stillDown = order.OutOfServiceAtUtc != null && order.BackInServiceAtUtc == null
                && order.Status != WorkOrderStatus.Cancelled,
            // Told to the client so a UI offers only what will actually work -
            // for this caller. Cancelling is left out for anyone but an
            // administrator, so the button is not offered only to be refused.
            allowedTransitions = WorkOrderTransitions.From(order.Status)
                .Where(s => s != WorkOrderStatus.Cancelled || CanCancel(principal))
                .ToList(),
            notes,
        });
    }

    private static async Task<IResult> SummaryAsync(HospitalPmDbContext db, CancellationToken ct)
        => Results.Ok(new
        {
            open = await db.WorkOrders.CountAsync(
                w => w.Status != WorkOrderStatus.Closed && w.Status != WorkOrderStatus.Cancelled, ct),
            critical = await db.WorkOrders.CountAsync(
                w => w.Priority == WorkOrderPriority.Critical
                     && w.Status != WorkOrderStatus.Closed
                     && w.Status != WorkOrderStatus.Cancelled, ct),
            unassigned = await db.WorkOrders.CountAsync(
                w => w.Status == WorkOrderStatus.Reported, ct),
            awaitingClosure = await db.WorkOrders.CountAsync(
                w => w.Status == WorkOrderStatus.Resolved, ct),
            machinesDown = await db.WorkOrders.CountAsync(
                w => w.OutOfServiceAtUtc != null && w.BackInServiceAtUtc == null, ct),
        });

    private static async Task<IResult> ReportAsync(
        [FromBody] ReportRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FaultDescription))
        {
            return Results.BadRequest(new { error = "Describe what is wrong with the machine." });
        }

        if (!await db.Equipment.AnyAsync(e => e.Id == request.EquipmentId, ct))
        {
            return Results.BadRequest(new { error = "Unknown equipment." });
        }

        var now = clock.GetUtcNow().UtcDateTime;

        var order = new WorkOrder
        {
            EquipmentId = request.EquipmentId,
            FaultDescription = request.FaultDescription.Trim(),
            Priority = Enum.IsDefined(request.Priority) ? request.Priority : WorkOrderPriority.Medium,
            Status = WorkOrderStatus.Reported,
            ReportedByUserId = UserId(principal),
            ReportedAtUtc = now,
            // Somebody reporting a machine is somebody saying it is not working, so the machine is
            // down from this moment. Downtime that started when an engineer got round to it would
            // understate every uptime figure the hospital reports.
            OutOfServiceAtUtc = now,
        };

        db.WorkOrders.Add(order);
        await db.SaveChangesAsync(ct);
        await db.Entry(order).ReloadAsync(ct);

        return Results.Created($"/api/work-orders/{order.Id}", new { order.Id, order.Number });
    }

    private static async Task<IResult> AssignAsync(
        int id,
        [FromBody] AssignRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        var order = await db.WorkOrders.SingleOrDefaultAsync(w => w.Id == id, ct);
        if (order is null)
        {
            return Results.NotFound();
        }

        if (WorkOrderTransitions.IsTerminal(order.Status))
        {
            return Results.Conflict(new { error = WorkOrderTransitions.Explain(order.Status, order.Status) });
        }

        if (request.AssignedToUserId is { } assignee)
        {
            if (!await db.Users.AnyAsync(u => u.Id == assignee && u.IsActive, ct))
            {
                return Results.BadRequest(new { error = "Unknown or inactive user." });
            }

            order.AssignedToUserId = assignee;
            order.AssignedAtUtc = clock.GetUtcNow().UtcDateTime;

            if (order.Status == WorkOrderStatus.Reported)
            {
                order.Status = WorkOrderStatus.Assigned;
            }
        }
        else
        {
            // Unassigned back to the pool: routine when someone is on leave.
            order.AssignedToUserId = null;
            order.AssignedAtUtc = null;

            if (order.Status == WorkOrderStatus.Assigned)
            {
                order.Status = WorkOrderStatus.Reported;
            }
        }

        AddNote(db, order, request.Note, principal, clock);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static bool CanCancel(ClaimsPrincipal principal) => principal.IsInRole(Roles.Admin);

    private static async Task<IResult> ChangeStatusAsync(
        int id,
        [FromBody] StatusRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        var order = await db.WorkOrders.SingleOrDefaultAsync(w => w.Id == id, ct);
        if (order is null)
        {
            return Results.NotFound();
        }

        if (request.Status == WorkOrderStatus.Resolved)
        {
            return Results.BadRequest(new
            {
                error = "Use the resolve endpoint, which requires resolution notes.",
            });
        }

        // Cancelling throws a reported fault out of the queue, and it is a dead
        // end: nothing moves out of Cancelled. That is a decision about what
        // gets done, the same kind as assigning the work or skipping a PM, and
        // those are an administrator's. An Employee could cancel a Critical
        // fault assigned to someone else and the server said yes.
        if (request.Status == WorkOrderStatus.Cancelled && !CanCancel(principal))
        {
            return Results.Json(
                new { error = "Only an administrator can cancel a work order. Add a note and ask one to." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (!WorkOrderTransitions.CanMove(order.Status, request.Status))
        {
            // The database enforces this too; checking here turns a raised
            // Postgres exception into a message an operator can act on.
            return Results.Conflict(new { error = WorkOrderTransitions.Explain(order.Status, request.Status) });
        }

        var now = clock.GetUtcNow().UtcDateTime;

        if (request.Status == WorkOrderStatus.InProgress && order.StartedAtUtc is null)
        {
            order.StartedAtUtc = now;
        }

        if (request.Status == WorkOrderStatus.Closed)
        {
            order.ClosedAtUtc = now;
        }

        // A machine flagged as unusable stays counted as down until the window
        // is closed, and the only thing that closed it was resolving the order.
        // Cancelling never did, and Cancelled is terminal, so a cancelled
        // machine-down fault left the machine "down" on the dashboard for good
        // with no work order left to clear it.
        if (request.Status == WorkOrderStatus.Cancelled
            && order.OutOfServiceAtUtc is not null && order.BackInServiceAtUtc is null)
        {
            order.BackInServiceAtUtc = now;
        }

        // "We thought it was fixed" and it was not: the machine is down again, and it has been down
        // since the report, so the hours are counted as one outage rather than two.
        if (order.Status == WorkOrderStatus.Resolved && request.Status == WorkOrderStatus.InProgress)
        {
            order.BackInServiceAtUtc = null;
        }

        order.Status = request.Status;
        AddNote(db, order, request.Note, principal, clock);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ResolveAsync(
        int id,
        [FromBody] ResolveRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ResolutionNotes))
        {
            // "Fixed" with no explanation teaches the next engineer nothing
            // and is worthless in a recurring-fault review.
            return Results.BadRequest(new { error = "Say what was wrong and what you did." });
        }

        var order = await db.WorkOrders.SingleOrDefaultAsync(w => w.Id == id, ct);
        if (order is null)
        {
            return Results.NotFound();
        }

        if (!WorkOrderTransitions.CanMove(order.Status, WorkOrderStatus.Resolved))
        {
            return Results.Conflict(new
            {
                error = WorkOrderTransitions.Explain(order.Status, WorkOrderStatus.Resolved),
            });
        }

        var now = clock.GetUtcNow().UtcDateTime;

        order.Status = WorkOrderStatus.Resolved;
        order.ResolutionNotes = request.ResolutionNotes.Trim();
        order.ResolvedByUserId = UserId(principal);
        order.ResolvedAtUtc = now;

        // Fixed means back in use: the downtime stops here and the uptime starts again.
        if (order.OutOfServiceAtUtc is not null && order.BackInServiceAtUtc is null)
        {
            order.BackInServiceAtUtc = now;
        }

        AddNote(db, order, request.ResolutionNotes, principal, clock);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { order.Status, order.DowntimeMinutes, DowntimeHours = Downtime.Hours(order.DowntimeMinutes ?? 0) });
    }

    private static async Task<IResult> AddNoteAsync(
        int id,
        [FromBody] NoteRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Body))
        {
            return Results.BadRequest(new { error = "The note is empty." });
        }

        var order = await db.WorkOrders.SingleOrDefaultAsync(w => w.Id == id, ct);
        if (order is null)
        {
            return Results.NotFound();
        }

        AddNote(db, order, request.Body, principal, clock, recordStatus: false);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static void AddNote(
        HospitalPmDbContext db,
        WorkOrder order,
        string? body,
        ClaimsPrincipal principal,
        TimeProvider clock,
        bool recordStatus = true)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        db.WorkOrderNotes.Add(new WorkOrderNote
        {
            WorkOrderId = order.Id,
            TenantId = order.TenantId,
            Body = body.Trim(),
            StatusAfter = recordStatus ? order.Status : null,
            AuthorUserId = UserId(principal),
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        });
    }

    private static int UserId(ClaimsPrincipal principal)
        => int.Parse(
            principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? "0",
            System.Globalization.CultureInfo.InvariantCulture);
}
