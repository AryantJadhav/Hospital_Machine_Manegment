using HospitalPm.Api.Auth;
using HospitalPm.Api.Equipment;
using System.Security.Claims;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.GatePasses;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// See EquipmentEndpoints: these expressions are LINQ-to-SQL, where ToLower() is
// what translates and the StringComparison overloads CA1862 asks for do not.
#pragma warning disable CA1862

namespace HospitalPm.Api.WorkOrders;

/// <summary>The machine to put in place of the faulty one, and anything worth saying about it.</summary>
public sealed record ReplacementRequest(int EquipmentId, string? Note);

/// <summary>Taking the stand-in back. The note is optional.</summary>
public sealed record ReturnReplacementRequest(string? Note);

/// <summary>One stand-in, as the request's page shows it. Never a cost.</summary>
public sealed record ReplacementResponse(
    int Id,
    int EquipmentId,
    string AssetTag,
    string TypeName,
    string? Manufacturer,
    string? Model,
    DateTime ProvidedAtUtc,
    string? ProvidedByName,
    string? Note,
    string PlacedAt,
    string FromLocationName,
    DateTime? ReturnedAtUtc,
    string? ReturnedByName,
    bool IsActive);

/// <summary>
/// A replacement machine for a faulty one. A repair can take days and the department still has its work to do, so the
/// engineer picks a free machine from the store, puts it where the faulty one stands, and takes it back when the repair is done.
///
/// Only a machine that is in store is offered: not one in use, under repair, lost or retired, not one that is out on a gate
/// pass, and not one that is already standing in for another fault. The server checks all of that again when the engineer
/// confirms, because two people can pick the same machine in the same minute.
/// </summary>
public static class WorkOrderReplacementEndpoints
{
    private const int MaxOptions = 50;

    public static void MapReplacementEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{id:int}/replacement-options", OptionsAsync).RequirePermission(Permissions.WorkOrdersWork);
        group.MapPost("/{id:int}/replacement", ProvideAsync).RequirePermission(Permissions.WorkOrdersWork);
        group.MapPost("/{id:int}/replacement/return", ReturnAsync).RequirePermission(Permissions.WorkOrdersWork);
    }

    /// <summary>
    /// What the request's page shows about stand-ins: every one there has been, newest first. Read for a department too, so it
    /// shows the machine and where it came from, never a cost. The caller has already loaded the request through the filtered
    /// set, so the request is the caller's to see; the machines on it are read without the department filter, because a stand-in
    /// that has gone back to store is no longer in the department's list.
    /// </summary>
    public static async Task<List<ReplacementResponse>> ListAsync(HospitalPmDbContext db, int workOrderId, CancellationToken ct)
    {
        return await (
            from r in db.WorkOrderReplacements.IgnoreQueryFilters().AsNoTracking()
            where r.WorkOrderId == workOrderId
            join e in db.Equipment.IgnoreQueryFilters() on r.ReplacementEquipmentId equals e.Id
            join t in db.EquipmentTypes on e.EquipmentTypeId equals t.Id
            join fl in db.Locations.IgnoreQueryFilters() on r.FromLocationId equals fl.Id
            join tl in db.Locations.IgnoreQueryFilters() on r.ToLocationId equals tl.Id
            orderby r.Id descending
            select new ReplacementResponse(
                r.Id,
                e.Id,
                e.AssetTag,
                t.Name,
                e.Manufacturer,
                e.Model,
                r.ProvidedAtUtc,
                db.Users.Where(u => u.Id == r.ProvidedByUserId).Select(u => u.FullName).FirstOrDefault(),
                r.Note,
                tl.Name,
                fl.Name,
                r.ReturnedAtUtc,
                db.Users.Where(u => u.Id == r.ReturnedByUserId).Select(u => u.FullName).FirstOrDefault(),
                r.ReturnedAtUtc == null)).ToListAsync(ct);
    }

    /// <summary>Why a stand-in cannot be provided for this machine at all, or null when it can.</summary>
    private static string? WhyNoReplacement(Domain.Assets.Equipment faulty) => faulty.Status switch
    {
        EquipmentStatus.Condemned => "This machine has been condemned, so there is nothing to replace.",
        EquipmentStatus.Disposed => $"{faulty.AssetTag} has been disposed of, so there is nothing to replace.",
        EquipmentStatus.Lost => $"{faulty.AssetTag} is recorded as lost. Deal with that on its loss record.",
        _ => null,
    };

    /// <summary>Whether this request can be given a stand-in right now. Told to the page so it offers only what will work.</summary>
    public static async Task<bool> CanProvideAsync(HospitalPmDbContext db, WorkOrder order, Domain.Assets.Equipment? faulty, CancellationToken ct)
    {
        if (faulty is null || WorkOrderTransitions.IsLocked(order.Status) || WhyNoReplacement(faulty) is not null)
        {
            return false;
        }

        return !await db.WorkOrderReplacements.IgnoreQueryFilters()
            .AnyAsync(r => r.WorkOrderId == order.Id && r.ReturnedAtUtc == null, ct);
    }

    /// <summary>
    /// The machines that could stand in: in store, free, not the faulty one. The same kind of machine comes first (and the same
    /// make and model before that), because a ventilator is replaced by a ventilator; "other kinds" widens the list for the
    /// engineer who has none and will make do.
    /// </summary>
    private static async Task<IResult> OptionsAsync(
        int id, HospitalPmDbContext db, CancellationToken ct, string? q = null, bool otherTypes = false)
    {
        var order = await db.WorkOrders.AsNoTracking()
            .Include(w => w.Equipment)!.ThenInclude(e => e!.EquipmentType)
            .SingleOrDefaultAsync(w => w.Id == id, ct);
        if (order?.Equipment is not { } faulty)
        {
            return Results.NotFound();
        }

        var make = (faulty.Manufacturer ?? string.Empty).Trim().ToLower();
        var model = (faulty.Model ?? string.Empty).Trim().ToLower();
        var typeId = faulty.EquipmentTypeId;

        var free = db.Equipment.AsNoTracking()
            .Where(e => e.Status == EquipmentStatus.InStore && e.Id != faulty.Id)
            // Out of the hospital on a gate pass, it is not here to be put anywhere.
            .Where(e => !db.GatePassItems.Any(i => i.EquipmentId == e.Id && i.GatePass!.Status == GatePassStatus.Out))
            // Already standing in for another fault.
            .Where(e => !db.WorkOrderReplacements.Any(r => r.ReplacementEquipmentId == e.Id && r.ReturnedAtUtc == null));

        if (!otherTypes)
        {
            free = free.Where(e => e.EquipmentTypeId == typeId);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            free = free.Where(e =>
                e.AssetTag.ToLower().Contains(term)
                || (e.Manufacturer != null && e.Manufacturer.ToLower().Contains(term))
                || (e.Model != null && e.Model.ToLower().Contains(term))
                || (e.SerialNumber != null && e.SerialNumber.ToLower().Contains(term)));
        }

        var total = await free.CountAsync(ct);

        var items = await free
            .Select(e => new
            {
                e.Id,
                e.AssetTag,
                typeName = e.EquipmentType!.Name,
                e.Manufacturer,
                e.Model,
                e.SerialNumber,
                locationName = e.Location!.Name,
                sameType = e.EquipmentTypeId == typeId,
                // The same make and model only counts for the same kind of machine.
                sameModel = e.EquipmentTypeId == typeId && make != "" && model != ""
                    && e.Manufacturer != null && e.Manufacturer.ToLower() == make
                    && e.Model != null && e.Model.ToLower() == model,
            })
            .OrderByDescending(x => x.sameModel).ThenByDescending(x => x.sameType).ThenBy(x => x.AssetTag)
            .Take(MaxOptions)
            .ToListAsync(ct);

        return Results.Ok(new
        {
            forMachine = new
            {
                faulty.AssetTag,
                typeName = faulty.EquipmentType?.Name,
                faulty.Manufacturer,
                faulty.Model,
                locationName = await db.Locations.AsNoTracking().Where(l => l.Id == faulty.LocationId).Select(l => l.Name).FirstOrDefaultAsync(ct),
            },
            items,
            total,
            otherTypes,
        });
    }

    private static async Task<IResult> ProvideAsync(
        int id,
        [FromBody] ReplacementRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (request.Note is { Length: > 500 })
        {
            return Results.BadRequest(new { error = "The note is too long. Keep it under 500 characters." });
        }

        var (order, transaction) = await WorkOrderEndpoints.LockAsync(db, id, ct);
        await using var _ = transaction;
        if (order is null)
        {
            return Results.NotFound();
        }

        if (WorkOrderTransitions.IsLocked(order.Status))
        {
            return Results.Conflict(new { error = WorkOrderTransitions.LockedBecause(order.Status) });
        }

        var faulty = await db.Equipment.SingleOrDefaultAsync(e => e.Id == order.EquipmentId, ct);
        if (faulty is null)
        {
            return Results.NotFound();
        }

        if (WhyNoReplacement(faulty) is { } why)
        {
            return Results.Conflict(new { error = why });
        }

        var inPlace = await db.WorkOrderReplacements
            .Where(r => r.WorkOrderId == id && r.ReturnedAtUtc == null)
            .Select(r => r.ReplacementEquipment!.AssetTag)
            .FirstOrDefaultAsync(ct);
        if (inPlace is not null)
        {
            return Results.Conflict(new { error = $"{inPlace} is already standing in for this machine. Take it back first to put another in its place." });
        }

        if (request.EquipmentId == faulty.Id)
        {
            return Results.BadRequest(new { error = "A machine cannot stand in for itself. Choose another machine." });
        }

        var spare = await db.Equipment.SingleOrDefaultAsync(e => e.Id == request.EquipmentId, ct);
        if (spare is null)
        {
            return Results.BadRequest(new { error = "That machine was not found. Choose one from the list." });
        }

        if (await FaultReporting.OpenPassAsync(db, spare.Id, ct) is { } pass)
        {
            return Results.Conflict(new { error = $"{spare.AssetTag} is out of the hospital on gate pass {pass}. Choose another machine." });
        }

        if (spare.Status != EquipmentStatus.InStore)
        {
            return Results.Conflict(new
            {
                error = $"{spare.AssetTag} is not free any more: it is {Describe(spare.Status)}. Choose another machine from the list.",
            });
        }

        var busy = await db.WorkOrderReplacements.IgnoreQueryFilters()
            .Where(r => r.ReplacementEquipmentId == spare.Id && r.ReturnedAtUtc == null)
            .Select(r => r.WorkOrder!.Number)
            .FirstOrDefaultAsync(ct);
        if (busy is not null)
        {
            return Results.Conflict(new { error = $"{spare.AssetTag} is already standing in for another machine ({busy}). Choose another." });
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var userId = EquipmentMoveEndpoints.UserId(principal);
        var from = spare.LocationId;
        var to = faulty.LocationId;

        var placedAt = await db.Locations.AsNoTracking().Where(l => l.Id == to).Select(l => l.Name).FirstOrDefaultAsync(ct) ?? "its place";
        var kind = string.Join(' ', new[] { spare.Manufacturer, spare.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));

        if (from != to)
        {
            EquipmentMoveEndpoints.Record(
                db, spare, to, $"Replacement for {faulty.AssetTag} ({order.Number}) while it is repaired", userId, now);
            spare.LocationId = to;
        }

        var statusBefore = spare.Status;
        spare.Status = EquipmentStatus.InService;

        var row = new WorkOrderReplacement
        {
            TenantId = order.TenantId,
            WorkOrderId = order.Id,
            ReplacementEquipmentId = spare.Id,
            FromLocationId = from,
            ToLocationId = to,
            StatusBefore = statusBefore,
            ProvidedAtUtc = now,
            ProvidedByUserId = userId,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
        };
        db.WorkOrderReplacements.Add(row);

        var words = $"Replacement machine provided: {spare.AssetTag}{(kind.Length > 0 ? $" ({kind})" : string.Empty)} was put at {placedAt} while {faulty.AssetTag} is repaired.";
        WorkOrderEndpoints.AddNote(db, order, words, principal, clock, recordStatus: false);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Someone else won the race for the same machine, or for this request, between the checks and the save.
            return Results.Conflict(new { error = "Someone else has just put a replacement in place. Reload the page and look again." });
        }

        await transaction.CommitAsync(ct);

        return Results.Created($"/api/work-orders/{id}", new { id = row.Id, assetTag = spare.AssetTag, placedAt });
    }

    private static async Task<IResult> ReturnAsync(
        int id,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct,
        [FromBody] ReturnReplacementRequest? request = null)
    {
        if (request?.Note is { Length: > 500 })
        {
            return Results.BadRequest(new { error = "The note is too long. Keep it under 500 characters." });
        }

        // Taking it back is allowed whatever state the request is in: the repair is often finished (the request resolved and
        // locked) before the stand-in is carried back.
        var (order, transaction) = await WorkOrderEndpoints.LockAsync(db, id, ct);
        await using var _ = transaction;
        if (order is null)
        {
            return Results.NotFound();
        }

        var row = await db.WorkOrderReplacements
            .Where(r => r.WorkOrderId == id && r.ReturnedAtUtc == null)
            .SingleOrDefaultAsync(ct);
        if (row is null)
        {
            return Results.Conflict(new { error = "No replacement machine is in place for this request." });
        }

        var spare = await db.Equipment.SingleAsync(e => e.Id == row.ReplacementEquipmentId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var userId = EquipmentMoveEndpoints.UserId(principal);

        var backTo = await db.Locations.AsNoTracking().Where(l => l.Id == row.FromLocationId).Select(l => l.Name).FirstOrDefaultAsync(ct) ?? "where it came from";
        var movedBack = false;

        // A machine that has since been lost or retired is not carried anywhere: only the loan is closed.
        var stillAMachine = spare.Status is not (EquipmentStatus.Lost or EquipmentStatus.Condemned or EquipmentStatus.Disposed);
        var outOnAPass = await FaultReporting.OpenPassAsync(db, spare.Id, ct) is not null;

        if (stillAMachine && !outOnAPass && spare.LocationId != row.FromLocationId)
        {
            EquipmentMoveEndpoints.Record(
                db, spare, row.FromLocationId, $"Replacement for {order.Number} taken back", userId, now);
            spare.LocationId = row.FromLocationId;
            movedBack = true;
        }

        // Back to what it was, but only if nothing has changed it since: a stand-in that has itself gone for repair stays so.
        if (spare.Status == EquipmentStatus.InService && row.StatusBefore != EquipmentStatus.InService)
        {
            spare.Status = row.StatusBefore;
        }

        row.ReturnedAtUtc = now;
        row.ReturnedByUserId = userId;

        // The request's own record gets a line only while it can still take one.
        if (!WorkOrderTransitions.IsLocked(order.Status))
        {
            var words = $"Replacement machine {spare.AssetTag} was taken back{(movedBack ? $" to {backTo}" : string.Empty)}."
                + (string.IsNullOrWhiteSpace(request?.Note) ? string.Empty : $" {request!.Note!.Trim()}");
            WorkOrderEndpoints.AddNote(db, order, words, principal, clock, recordStatus: false);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return Results.NoContent();
    }

    private static string Describe(EquipmentStatus status) => status switch
    {
        EquipmentStatus.InService => "in use",
        EquipmentStatus.UnderRepair => "under repair",
        EquipmentStatus.Condemned => "condemned",
        EquipmentStatus.Disposed => "disposed of",
        EquipmentStatus.Lost => "recorded as lost",
        _ => "in store",
    };
}
