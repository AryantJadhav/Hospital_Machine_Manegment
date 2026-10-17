using HospitalPm.Api.Auth;
using System.Security.Claims;
using HospitalPm.Api.Maintenance;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace HospitalPm.Api.WorkOrders;

public sealed record UsePartRequest(int SparePartId, int QuantityUsed);

public sealed record WorkOrderPhotoResponse(
    int Id,
    string FileName,
    string ContentType,
    int SizeBytes,
    string? UploadedBy,
    DateTime UploadedAtUtc);

public sealed record ReportRequest(
    int EquipmentId,
    string FaultDescription,
    WorkOrderPriority Priority,
    // Hardware, software, both, an accessory or consumable, or improper usage. Left out when it is not known yet.
    int? BreakdownType = null);

/// <summary>What kind of breakdown it was, or null to take it off.</summary>
public sealed record BreakdownTypeRequest(int? BreakdownType);

public sealed record AssignRequest(int? AssignedToUserId, string? Note);

public sealed record ResolveRequest(string ResolutionNotes);

public sealed record StatusRequest(WorkOrderStatus Status, string? Note);

/// <summary>The department's answer to a resolved fault: it is fixed (accepted), or it is not (a reason is needed).</summary>
public sealed record AcknowledgeRequest(bool Accepted, string? Note);

public sealed record NoteRequest(string Body);

public static class WorkOrderEndpoints
{
    private const int MaxPageSize = 200;

    public static void MapWorkOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/work-orders").WithTags("Work orders").RequirePermission(Permissions.WorkOrdersView);

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapGet("/summary", SummaryAsync);

        // Anyone who works the floor can report a fault. A ward that cannot
        // raise a ticket phones the biomedical department instead, and the
        // fault never enters the record at all.
        group.MapPost("/", ReportAsync).RequirePermission(Permissions.WorkOrdersReport);
        group.MapPost("/{id:int}/notes", AddNoteAsync).RequirePermission(Permissions.WorkOrdersNote);
        group.MapPost("/{id:int}/status", ChangeStatusAsync).RequirePermission(Permissions.WorkOrdersWork);
        // After an engineer resolves a fault it goes back to the department that asked, to say whether it is really fixed.
        group.MapPost("/{id:int}/acknowledge", AcknowledgeAsync).RequirePermission(Permissions.WorkOrdersAcknowledge);
        group.MapPost("/{id:int}/resolve", ResolveAsync).RequirePermission(Permissions.WorkOrdersWork);

        // What kind of breakdown it was is the engineer's to say, once they have looked at the machine.
        group.MapPut("/{id:int}/breakdown-type", SetBreakdownTypeAsync).RequirePermission(Permissions.WorkOrdersWork);

        // Drawing a part off the shelf is part of doing the repair, the same
        // authority as adding a note or changing status - not a supervisory
        // decision like assignment.
        group.MapPost("/{id:int}/parts", UsePartAsync).RequirePermission(Permissions.WorkOrdersWork);
        group.MapDelete("/{id:int}/parts/{partUsageId:int}", RemovePartAsync).RequirePermission(Permissions.WorkOrdersWork);

        // A photo of the fault or the repair is part of the ticket, the same
        // authority as a note. Removing one is an Administrator's, the same
        // as a PM's report file: it is evidence, not for whoever holds the
        // list, but a photo that should never have been taken has to go.
        group.MapPost("/{id:int}/photos", AddPhotosAsync).DisableAntiforgery().RequirePermission(Permissions.WorkOrdersNote);
        group.MapGet("/photos/{attachmentId:int}", DownloadPhotoAsync);
        group.MapDelete("/photos/{attachmentId:int}", DeletePhotoAsync)
            .RequirePermission(Permissions.AttachmentsDelete);

        // Assignment is a supervisory decision about who does the work.
        group.MapPost("/{id:int}/assign", AssignAsync)
            .RequirePermission(Permissions.WorkOrdersAssign);
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

    /// <summary>
    /// How long a request whose repair is done stays on a department's list. The team that did the work
    /// takes it off its queue the moment it is resolved; the department that asked is told the answer, and
    /// should be able to see it for long enough to notice, so it stays for a week.
    /// </summary>
    public static readonly TimeSpan DoneStaysOnTheDepartmentsList = TimeSpan.FromDays(7);

    private static async Task<IResult> ListAsync(
        HospitalPmDbContext db,
        TimeProvider clock,
        ClaimsPrincipal principal,
        [FromQuery] WorkOrderStatus? status,
        [FromQuery] WorkOrderPriority? priority,
        [FromQuery] int? breakdownType,
        [FromQuery] int? equipmentId,
        [FromQuery] int? assignedToUserId,
        [FromQuery] string? assignee,
        // "me": only the requests this person raised. Worked out here, from the token, like assignee.
        [FromQuery] string? requestedBy,
        [FromQuery] string? q,
        [FromQuery] bool? down,
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

        if (string.Equals(requestedBy, "me", StringComparison.OrdinalIgnoreCase))
        {
            var me = UserId(principal);
            query = query.Where(w => w.ReportedByUserId == me);
        }

        if (status is not null)
        {
            query = query.Where(w => w.Status == status);
        }
        else if (mine && openOnly)
        {
            query = query.Where(w => OnTheAssigneesPlate.Contains(w.Status));
        }
        else if (openOnly && down != true)
        {
            // Once the fault is fixed it has left the queue: Resolved is waiting on paperwork, not on
            // an engineer. It is still there under the Resolved filter, and in the machine's history.
            if (db.IsScoped)
            {
                // A person from another department also sees the requests whose repair was done in the
                // last week, so the answer to what they asked does not vanish before they have seen it.
                var since = clock.GetUtcNow().UtcDateTime - DoneStaysOnTheDepartmentsList;
                query = query.Where(w => w.Status != WorkOrderStatus.Cancelled
                    && (w.Status != WorkOrderStatus.Resolved && w.Status != WorkOrderStatus.Closed
                        || (w.ResolvedAtUtc ?? w.ClosedAtUtc) >= since));
            }
            else
            {
                query = query.Where(w => w.Status != WorkOrderStatus.Resolved
                                      && w.Status != WorkOrderStatus.Closed
                                      && w.Status != WorkOrderStatus.Cancelled);
            }
        }

        // The faults behind the dashboard's "Machines down": a machine taken out of service that
        // has not yet gone back. Counted the same way there, so the tile and this list agree.
        if (down == true)
        {
            query = query.Where(w => w.OutOfServiceAtUtc != null && w.BackInServiceAtUtc == null);
        }

        if (breakdownType is { } kind)
        {
            if (!Enum.IsDefined((BreakdownType)kind))
            {
                return Results.BadRequest(new { error = "Unknown breakdown type." });
            }

            query = query.Where(w => (int?)w.BreakdownType == kind);
        }

        if (priority is not null)
        {
            query = query.Where(w => w.Priority == priority);
        }

        // By number, by the machine's tag, or by what was said was wrong: the three things a person
        // has in their head when they go looking for a ticket.
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLowerInvariant();
            query = query.Where(w =>
                w.Number.ToLower().Contains(term) ||
                w.Equipment!.AssetTag.ToLower().Contains(term) ||
                w.FaultDescription.ToLower().Contains(term));
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
                BreakdownType = (int?)w.BreakdownType,
                w.EquipmentId,
                w.Equipment!.AssetTag,
                EquipmentTypeName = w.Equipment!.EquipmentType!.Name,
                LocationName = w.Equipment!.Location!.Name,
                w.ReportedAtUtc,
                w.ResolvedAtUtc,
                w.ReportedByUserId,
                ReportedByName = db.Users.Where(u => u.Id == w.ReportedByUserId).Select(u => u.FullName).FirstOrDefault(),
                w.AssignedToUserId,
                AssignedToName = w.AssignedToUserId == null
                    ? null
                    : db.Users.Where(u => u.Id == w.AssignedToUserId).Select(u => u.FullName).FirstOrDefault(),
                w.OutOfServiceAtUtc,
                w.BackInServiceAtUtc,
                w.StartedAtUtc,
            })
            .ToListAsync(ct);

        var targets = await ResponseTargetEndpoints.LoadAsync(db, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        var shaped = items.Select(w => new
        {
            w.Id,
            w.Number,
            w.Status,
            w.Priority,
            w.FaultDescription,
            w.BreakdownType,
            w.EquipmentId,
            w.AssetTag,
            w.EquipmentTypeName,
            w.LocationName,
            w.ReportedAtUtc,
            w.ResolvedAtUtc,
            w.ReportedByUserId,
            w.ReportedByName,
            w.AssignedToUserId,
            w.AssignedToName,
            w.OutOfServiceAtUtc,
            w.BackInServiceAtUtc,
            response = ResponseOf(w.ReportedAtUtc, w.StartedAtUtc, w.Priority, w.Status, targets, now),
        }).ToList();

        return Results.Ok(new { items = shaped, total, page, pageSize });
    }

    /// <summary>
    /// Where a fault stands against the response time its priority asks for: raised to In progress. The same answer in the
    /// list and on the fault's own page.
    /// </summary>
    private static object ResponseOf(
        DateTime reportedAtUtc,
        DateTime? startedAtUtc,
        WorkOrderPriority priority,
        WorkOrderStatus status,
        IReadOnlyDictionary<WorkOrderPriority, int> targets,
        DateTime nowUtc)
    {
        var a = ResponseRules.Assess(
            reportedAtUtc, startedAtUtc, targets.TryGetValue(priority, out var minutes) ? minutes : null, status, nowUtc);

        return new
        {
            state = a.State.ToString(),
            targetMinutes = a.TargetMinutes,
            dueAtUtc = a.DueAtUtc,
            responseMinutes = a.ResponseMinutes,
            overdueMinutes = a.OverdueMinutes,
            startedAtUtc,
        };
    }

    private static async Task<IResult> GetAsync(
        int id, HospitalPmDbContext db, ClaimsPrincipal principal, PermissionService permissions, TimeProvider clock, CancellationToken ct)
    {
        var mayCancel = await permissions.CanAsync(principal, Permissions.WorkOrdersCancel, ct);
        var mayWork = await permissions.CanAsync(principal, Permissions.WorkOrdersWork, ct);
        var mayReview = await permissions.CanAsync(principal, Permissions.WorkOrdersReview, ct);
        var mayAcknowledge = await permissions.CanAsync(principal, Permissions.WorkOrdersAcknowledge, ct);

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
            .Select(n => new
            {
                n.Id,
                n.Body,
                n.StatusAfter,
                n.AuthorUserId,
                // The page shows who wrote it, and a number is no use to the person reading.
                AuthorName = db.Users.Where(u => u.Id == n.AuthorUserId).Select(u => u.FullName).FirstOrDefault(),
                n.CreatedAtUtc,
            })
            .ToListAsync(ct);

        var reportedByName = await db.Users.AsNoTracking()
            .Where(u => u.Id == order.ReportedByUserId).Select(u => u.FullName).FirstOrDefaultAsync(ct);
        var assignedToName = order.AssignedToUserId is { } assigneeId
            ? await db.Users.AsNoTracking().Where(u => u.Id == assigneeId).Select(u => u.FullName).FirstOrDefaultAsync(ct)
            : null;

        // A person in another department is told which parts were used, and not what they cost.
        var hideCosts = db.IsScoped;

        var partsUsed = await db.WorkOrderParts.AsNoTracking()
            .Where(p => p.WorkOrderId == id)
            .OrderBy(p => p.UsedAtUtc)
            .Select(p => new
            {
                p.Id,
                p.SparePartId,
                p.SparePart!.PartNumber,
                p.SparePart.Name,
                p.QuantityUsed,
                UnitCostAtUse = hideCosts ? (decimal?)null : p.UnitCostAtUse,
                p.UsedByUserId,
                p.UsedAtUtc,
            })
            .ToListAsync(ct);

        var photos = await db.WorkOrderAttachments.AsNoTracking()
            .Where(a => a.WorkOrderId == id)
            .OrderBy(a => a.UploadedAtUtc).ThenBy(a => a.Id)
            .Select(a => new WorkOrderPhotoResponse(
                a.Id,
                a.FileName,
                a.ContentType,
                a.SizeBytes,
                db.Users.Where(u => u.Id == a.UploadedByUserId).Select(u => u.FullName).FirstOrDefault(),
                a.UploadedAtUtc))
            .ToListAsync(ct);

        var responseTargets = await ResponseTargetEndpoints.LoadAsync(db, ct);

        return Results.Ok(new
        {
            order.Id,
            order.Number,
            order.Status,
            order.Priority,
            order.FaultDescription,
            breakdownType = (int?)order.BreakdownType,
            response = ResponseOf(
                order.ReportedAtUtc, order.StartedAtUtc, order.Priority, order.Status, responseTargets, clock.GetUtcNow().UtcDateTime),
            order.EquipmentId,
            assetTag = order.Equipment?.AssetTag,
            // The list endpoint returns these and the detail panel shows the
            // same header, so omitting them here left it rendering
            // "BME-0001 · ·" with two empty separators.
            equipmentTypeName = order.Equipment?.EquipmentType?.Name,
            locationName = order.Equipment?.Location?.Name,
            order.ReportedByUserId,
            reportedByName,
            order.ReportedAtUtc,
            order.AssignedToUserId,
            assignedToName,
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
            // The department that asked can answer a resolved fault: accept it or say it is not fixed.
            canAcknowledge = order.Status == WorkOrderStatus.Resolved && mayAcknowledge,
            allowedTransitions = !mayWork || (order.Status == WorkOrderStatus.Resolved && !mayReview)
                ? []
                : WorkOrderTransitions.From(order.Status)
                    .Where(s => s != WorkOrderStatus.Cancelled || mayCancel)
                    // In progress is not offered to someone who may not start it: the fault is someone else's.
                    .Where(s => s != WorkOrderStatus.InProgress || HeldByAnother(order.Status, order.AssignedToUserId, UserId(principal)) is false)
                    .ToList(),
            notes,
            partsUsed,
            photos,
        });
    }

    private static async Task<IResult> SummaryAsync(HospitalPmDbContext db, CancellationToken ct)
        => Results.Ok(new
        {
            open = await db.WorkOrders.CountAsync(
                w => w.Status != WorkOrderStatus.Resolved
                     && w.Status != WorkOrderStatus.Closed
                     && w.Status != WorkOrderStatus.Cancelled, ct),
            critical = await db.WorkOrders.CountAsync(
                w => w.Priority == WorkOrderPriority.Critical
                     && w.Status != WorkOrderStatus.Resolved
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

        var machineStatus = await db.Equipment.AsNoTracking()
            .Where(e => e.Id == request.EquipmentId)
            .Select(e => (EquipmentStatus?)e.Status)
            .SingleOrDefaultAsync(ct);

        if (machineStatus is null)
        {
            return Results.BadRequest(new { error = "Unknown equipment." });
        }

        // A condemned machine is withdrawn from service and kept on the register for its history. A fault
        // on it would sit in "machines down" and "open requests" for ever, with nobody able to repair it.
        if (machineStatus == EquipmentStatus.Condemned)
        {
            return Results.Conflict(new { error = "This machine has been condemned, so a fault cannot be reported on it." });
        }

        if (request.BreakdownType is { } kind && !Enum.IsDefined((BreakdownType)kind))
        {
            return Results.BadRequest(new { error = "Choose one of the breakdown types, or leave it out." });
        }

        var now = clock.GetUtcNow().UtcDateTime;

        var order = new WorkOrder
        {
            EquipmentId = request.EquipmentId,
            FaultDescription = request.FaultDescription.Trim(),
            BreakdownType = (BreakdownType?)request.BreakdownType,
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

    /// <summary>
    /// Says what kind of breakdown a request was, or takes it off. For a request that is still open: once it is
    /// closed or cancelled it is a record. The change goes on the request's timeline with who made it.
    /// </summary>
    private static async Task<IResult> SetBreakdownTypeAsync(
        int id,
        [FromBody] BreakdownTypeRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Held for the whole change: see LockAsync.
        var (order, transaction) = await LockAsync(db, id, ct);
        await using var lockScope = transaction;
        if (order is null)
        {
            return Results.NotFound();
        }

        if (WorkOrderTransitions.IsLocked(order.Status))
        {
            return Results.Conflict(new { error = WorkOrderTransitions.LockedBecause(order.Status) });
        }

        if (request.BreakdownType is { } kind && !Enum.IsDefined((BreakdownType)kind))
        {
            return Results.BadRequest(new { error = "Choose one of the breakdown types, or leave it blank." });
        }

        var next = (BreakdownType?)request.BreakdownType;
        if (next == order.BreakdownType)
        {
            return Results.NoContent();
        }

        order.BreakdownType = next;
        order.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;

        db.WorkOrderNotes.Add(new WorkOrderNote
        {
            WorkOrderId = order.Id,
            Body = next is { } set ? $"Breakdown type: {BreakdownTypeWords.Label(set)}." : "Breakdown type taken off.",
            AuthorUserId = UserId(principal),
            CreatedAtUtc = order.UpdatedAtUtc,
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> AssignAsync(
        int id,
        [FromBody] AssignRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Held for the whole change: see LockAsync.
        var (order, transaction) = await LockAsync(db, id, ct);
        await using var lockScope = transaction;
        if (order is null)
        {
            return Results.NotFound();
        }

        if (WorkOrderTransitions.IsLocked(order.Status))
        {
            return Results.Conflict(new { error = WorkOrderTransitions.LockedBecause(order.Status) });
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
        await transaction.CommitAsync(ct);

        return Results.NoContent();
    }


    /// <summary>
    /// Opens a transaction and takes the work order's row for the length of it (SELECT ... FOR UPDATE).
    ///
    /// Two people acting on the same service request in the same instant, say one cancelling it while the
    /// other resolves it, both read it as "in progress", both passed the check that the move is allowed, and
    /// whoever saved last won: the order ended up Resolved over a Cancelled, with two sets of notes. With the
    /// row held, the second one waits, then reads the state the first left, and gets the ordinary answer
    /// ("this service request is already resolved"). No schema change, and the message is the usual one.
    ///
    /// The caller disposes the transaction (an early return rolls back, having written nothing) and commits it
    /// after saving.
    /// </summary>
    private static async Task<(WorkOrder? Order, IDbContextTransaction Transaction)> LockAsync(
        HospitalPmDbContext db, int id, CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        var order = await db.WorkOrders
            .FromSqlInterpolated($"SELECT * FROM work_order WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        return (order, transaction);
    }

    /// <summary>
    /// Whether this fault is someone else's to start. In progress is how an engineer takes a fault: the first
    /// person to press it on a fault nobody has gets it, and from then on it is theirs. A fault the Head has
    /// already given to a named person is that person's to start, and nobody else's, until the Head reassigns
    /// it. Reopening a resolved fault is not taking it, so it is open to the Head and to anyone who may work.
    /// </summary>
    private static bool HeldByAnother(WorkOrderStatus status, int? assignedTo, int caller)
        => status != WorkOrderStatus.Resolved && assignedTo is { } holder && holder != caller;

    private static async Task<IResult> ChangeStatusAsync(
        int id,
        [FromBody] StatusRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        PermissionService permissions,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Held for the whole change: see LockAsync.
        var (order, transaction) = await LockAsync(db, id, ct);
        await using var lockScope = transaction;
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

        // A resolved request is locked. Only the head of the department can move it on: close it, or reopen it, and
        // reopening needs a reason, written on the request.
        if (order.Status == WorkOrderStatus.Resolved)
        {
            if (!await permissions.CanAsync(principal, Permissions.WorkOrdersReview, ct))
            {
                return Results.Json(
                    new { error = WorkOrderTransitions.LockedBecause(order.Status) },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (request.Status == WorkOrderStatus.InProgress && string.IsNullOrWhiteSpace(request.Note))
            {
                return Results.BadRequest(new { error = "Say why it is being reopened, in a few words." });
            }
        }

        // Cancelling throws a reported fault out of the queue, and it is a dead
        // end: nothing moves out of Cancelled. That is a decision about what
        // gets done, the same kind as assigning the work or skipping a PM, and
        // those are an administrator's. An Employee could cancel a Critical
        // fault assigned to someone else and the server said yes.
        if (request.Status == WorkOrderStatus.Cancelled
            && !await permissions.CanAsync(principal, Permissions.WorkOrdersCancel, ct))
        {
            return Results.Json(
                new { error = "Only an administrator can cancel a service request. Add a note and ask one to." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        // Said before the general check so the second person is told who has it, not just "that move is not
        // allowed". This runs under the row lock, so "first" is exact: of two people pressing In progress in
        // the same instant, one gets the fault and the other reads that it is taken.
        var caller = UserId(principal);
        if (request.Status == WorkOrderStatus.InProgress && HeldByAnother(order.Status, order.AssignedToUserId, caller))
        {
            var holder = await db.Users.AsNoTracking()
                .Where(u => u.Id == order.AssignedToUserId)
                .Select(u => u.FullName)
                .SingleOrDefaultAsync(ct) ?? "someone else";

            return Results.Conflict(new
            {
                error = order.Status == WorkOrderStatus.InProgress
                    ? $"{holder} is already working on this. Ask the Head of Biomedical to reassign it if it should be yours."
                    : $"This is assigned to {holder}, so only they can start it. Ask the Head of Biomedical to reassign it if it should be yours.",
            });
        }

        if (!WorkOrderTransitions.CanMove(order.Status, request.Status))
        {
            // The database enforces this too; checking here turns a raised
            // Postgres exception into a message an operator can act on.
            return Results.Conflict(new { error = WorkOrderTransitions.Explain(order.Status, request.Status) });
        }

        var now = clock.GetUtcNow().UtcDateTime;

        // Pressing In progress on a fault nobody has takes it: it is now this person's.
        if (request.Status == WorkOrderStatus.InProgress && order.AssignedToUserId is null)
        {
            order.AssignedToUserId = caller;
            order.AssignedAtUtc = now;
            db.WorkOrderNotes.Add(new WorkOrderNote
            {
                WorkOrderId = order.Id,
                TenantId = order.TenantId,
                Body = "Taken by the engineer who started it.",
                AuthorUserId = caller,
                CreatedAtUtc = now,
            });
        }

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
        await transaction.CommitAsync(ct);

        return Results.NoContent();
    }

    /// <summary>
    /// The department that asked answers a resolved fault. Accepted: it is closed, and locked for good. Not fixed: it goes
    /// straight back to the engineer, In progress, with the department's reason written on it, and the machine counts as down
    /// again. The department scope already hides every fault outside the person's own departments (a 404), and an engineer
    /// does not hold the permission, so nobody marks their own work as accepted.
    /// </summary>
    private static async Task<IResult> AcknowledgeAsync(
        int id,
        [FromBody] AcknowledgeRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Held for the whole change: see LockAsync.
        var (order, transaction) = await LockAsync(db, id, ct);
        await using var lockScope = transaction;
        if (order is null)
        {
            return Results.NotFound();
        }

        if (order.Status != WorkOrderStatus.Resolved)
        {
            return Results.Conflict(new { error = "This service request is not waiting for the department's answer." });
        }

        var said = request.Note?.Trim() ?? string.Empty;
        var now = clock.GetUtcNow().UtcDateTime;

        if (request.Accepted)
        {
            order.Status = WorkOrderStatus.Closed;
            order.ClosedAtUtc = now;
            order.UpdatedAtUtc = now;
            AddNote(db, order, said.Length > 0 ? $"Accepted by the department. {said}" : "Accepted by the department.", principal, clock);
        }
        else
        {
            if (said.Length < 3)
            {
                return Results.BadRequest(new { error = "Say what is still wrong, in a few words." });
            }

            // The same as reopening: the machine is down again, counted from the report, not as a second outage.
            order.Status = WorkOrderStatus.InProgress;
            order.BackInServiceAtUtc = null;
            order.UpdatedAtUtc = now;
            AddNote(db, order, $"Not fixed, says the department: {said}", principal, clock);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
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

        // Held for the whole change: see LockAsync.
        var (order, transaction) = await LockAsync(db, id, ct);
        await using var lockScope = transaction;
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
        await transaction.CommitAsync(ct);

        return Results.Ok(new { order.Status, order.DowntimeMinutes, DowntimeHours = Downtime.Hours(order.DowntimeMinutes ?? 0) });
    }

    private static async Task<IResult> UsePartAsync(
        int id,
        [FromBody] UsePartRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (request.QuantityUsed <= 0)
        {
            return Results.BadRequest(new { error = "Quantity used must be at least 1." });
        }

        // Held for the whole change: see LockAsync.
        var (order, transaction) = await LockAsync(db, id, ct);
        await using var lockScope = transaction;
        if (order is null)
        {
            return Results.NotFound();
        }

        if (WorkOrderTransitions.IsLocked(order.Status))
        {
            return Results.Conflict(new { error = WorkOrderTransitions.LockedBecause(order.Status) });
        }

        var part = await db.SpareParts.SingleOrDefaultAsync(p => p.Id == request.SparePartId, ct);
        if (part is null)
        {
            return Results.BadRequest(new { error = "Unknown spare part." });
        }

        if (!part.IsActive)
        {
            return Results.BadRequest(new { error = "This part has been retired and can no longer be recorded as used." });
        }

        // Taken off the shelf by the database in one statement, not read here, subtracted and written
        // back. Two engineers recording the last fuse at the same moment both used to read "5", and both
        // wrote "4": eight uses were recorded and the shelf lost one. The condition stops the count going
        // below nothing, and the usage row goes in the same transaction so the two can never disagree.
        var taken = await db.SpareParts
            .Where(p => p.Id == part.Id && p.QuantityOnHand >= request.QuantityUsed)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.QuantityOnHand, p => p.QuantityOnHand - request.QuantityUsed), ct);

        if (taken == 0)
        {
            var left = await db.SpareParts.AsNoTracking()
                .Where(p => p.Id == part.Id)
                .Select(p => p.QuantityOnHand)
                .SingleAsync(ct);

            return Results.BadRequest(new
            {
                error = $"Only {left} {part.Unit} of {part.Name} left on the shelf.",
            });
        }

        db.WorkOrderParts.Add(new WorkOrderPart
        {
            WorkOrderId = order.Id,
            SparePartId = part.Id,
            QuantityUsed = request.QuantityUsed,
            UnitCostAtUse = part.UnitCost,
            UsedByUserId = UserId(principal),
            UsedAtUtc = clock.GetUtcNow().UtcDateTime,
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> RemovePartAsync(
        int id, int partUsageId, HospitalPmDbContext db, CancellationToken ct)
    {
        // Held for the whole change: see LockAsync.
        var (order, transaction) = await LockAsync(db, id, ct);
        await using var lockScope = transaction;
        if (order is null)
        {
            return Results.NotFound();
        }

        var usage = await db.WorkOrderParts.SingleOrDefaultAsync(p => p.Id == partUsageId && p.WorkOrderId == id, ct);
        if (usage is null)
        {
            return Results.NotFound();
        }

        if (WorkOrderTransitions.IsLocked(order.Status))
        {
            return Results.Conflict(new { error = WorkOrderTransitions.LockedBecause(order.Status) });
        }

        // Undo means the shelf gets the quantity back - the part was never really consumed.
        // Added back by the database, for the same reason a use is taken off by it.
        await db.SpareParts
            .Where(p => p.Id == usage.SparePartId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.QuantityOnHand, p => p.QuantityOnHand + usage.QuantityUsed), ct);

        db.WorkOrderParts.Remove(usage);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Results.NoContent();
    }

    /// <summary>Ten photos of ten megabytes each in one request is a mistake, not a real upload.</summary>
    private const long MaxPhotoRequestBytes = 40L * 1024 * 1024;

    /// <summary>Most tickets carry one or two photos. Ten is a generous ceiling, not a target.</summary>
    private const int MaxPhotosPerWorkOrder = 10;

    private static async Task<IResult> AddPhotosAsync(
        int id,
        HttpRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!request.HasFormContentType || request.ContentLength > MaxPhotoRequestBytes)
        {
            return Results.BadRequest(new { error = "Send the photos as a form." });
        }

        var order = await db.WorkOrders.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id, ct);
        if (order is null)
        {
            return Results.NotFound();
        }

        if (WorkOrderTransitions.IsLocked(order.Status))
        {
            return Results.Conflict(new { error = WorkOrderTransitions.LockedBecause(order.Status) });
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        {
            return Results.BadRequest(new { error = "The upload could not be read. Choose the photos again." });
        }

        if (form.Files.Count == 0)
        {
            return Results.BadRequest(new { error = "Choose a photo to save." });
        }

        var stored = await db.WorkOrderAttachments.CountAsync(a => a.WorkOrderId == id, ct);
        if (stored + form.Files.Count > MaxPhotosPerWorkOrder)
        {
            return Results.BadRequest(new
            {
                error = $"A service request can have at most {MaxPhotosPerWorkOrder} photos.",
            });
        }

        var accepted = new List<(string Name, string Type, byte[] Bytes)>();
        foreach (var file in form.Files)
        {
            if (file.Length == 0)
            {
                return Results.BadRequest(new { error = $"'{Path.GetFileName(file.FileName)}' is empty." });
            }

            if (file.Length > ReportFile.MaxBytes)
            {
                return Results.BadRequest(new
                {
                    error = $"'{Path.GetFileName(file.FileName)}' is larger than {ReportFile.MaxBytes / (1024 * 1024)} MB.",
                });
            }

            using var buffer = new MemoryStream((int)file.Length);
            await file.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();

            // A photo only - a work order's attachment is what someone saw, not a document. A PDF
            // belongs on the PM report instead.
            var type = ReportFile.Sniff(bytes);
            if (type is null || type == ReportFile.Pdf)
            {
                return Results.BadRequest(new
                {
                    error = $"'{Path.GetFileName(file.FileName)}' is not a photo (JPEG, PNG or WebP).",
                });
            }

            accepted.Add((ReportFile.CleanName(file.FileName, type), type, bytes));
        }

        var userId = UserId(principal);
        var now = clock.GetUtcNow().UtcDateTime;

        foreach (var (name, type, bytes) in accepted)
        {
            db.WorkOrderAttachments.Add(new WorkOrderAttachment
            {
                WorkOrderId = id,
                FileName = name,
                ContentType = type,
                SizeBytes = bytes.Length,
                UploadedByUserId = userId,
                UploadedAtUtc = now,
                Data = new WorkOrderAttachmentData { Data = bytes },
            });
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(new { photosSaved = accepted.Count });
    }

    private static async Task<IResult> DownloadPhotoAsync(int attachmentId, HttpContext http, HospitalPmDbContext db, CancellationToken ct)
    {
        var file = await db.WorkOrderAttachments.AsNoTracking()
            .Where(a => a.Id == attachmentId)
            .Select(a => new { a.FileName, a.ContentType, a.Data!.Data })
            .SingleOrDefaultAsync(ct);

        if (file is null)
        {
            return Results.NotFound();
        }

        // Shown in the browser, but only ever as the type the bytes were found to be.
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Content-Disposition"] =
            $"inline; filename=\"photo\"; filename*=UTF-8''{Uri.EscapeDataString(file.FileName)}";

        return Results.Bytes(file.Data, file.ContentType);
    }

    private static async Task<IResult> DeletePhotoAsync(int attachmentId, HospitalPmDbContext db, CancellationToken ct)
    {
        var attachment = await db.WorkOrderAttachments.SingleOrDefaultAsync(a => a.Id == attachmentId, ct);
        if (attachment is null)
        {
            return Results.NotFound();
        }

        // The bytes go with it; the audit log keeps a note that it was there and who removed it.
        db.WorkOrderAttachments.Remove(attachment);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
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

        if (WorkOrderTransitions.IsLocked(order.Status))
        {
            return Results.Conflict(new { error = WorkOrderTransitions.LockedBecause(order.Status) });
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
