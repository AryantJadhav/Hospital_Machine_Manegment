using System.Security.Claims;
using HospitalPm.Api.Auth;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using HospitalPm.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.WorkOrders;

public sealed record ResponseTargetRequest(WorkOrderPriority Priority, int Minutes);

public sealed record ResponseTargetsRequest(List<ResponseTargetRequest>? Targets);

/// <summary>The time the engineer really started, and why the recorded one is being changed.</summary>
public sealed record StartedTimeRequest(DateTime StartedAtUtc, string? Reason);

/// <summary>
/// How quickly an engineer is expected to respond to a fault, by priority: from the fault being raised to the engineer
/// pressing In progress. Everyone who can see work orders sees the targets (each fault is judged against them); only the
/// head of the department changes them (<see cref="Permissions.ResponseTimesEdit"/>).
/// </summary>
public static class ResponseTargetEndpoints
{
    public static void MapResponseTargetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/work-orders/response-targets")
            .WithTags("Work orders")
            .RequirePermission(Permissions.WorkOrdersView);

        group.MapGet("/", ListAsync);
        group.MapPut("/", SaveAsync).RequirePermission(Permissions.ResponseTimesEdit);

        // The head corrects the started time of a solved fault, so a late or forgotten In progress click is not held against
        // the engineer (or hidden). Recorded as a note on the fault, and by the audit log.
        app.MapPut("/api/work-orders/{id:int}/started-time", CorrectStartedAsync)
            .WithTags("Work orders")
            .RequirePermission(Permissions.ResponseTimesEdit);
    }

    private static async Task<IResult> CorrectStartedAsync(
        int id,
        [FromBody] StartedTimeRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        HospitalClock hospital,
        CancellationToken ct)
    {
        if (PermissionService.UserIdOf(principal) is not { } author)
        {
            return Results.Unauthorized();
        }

        var order = await db.WorkOrders.SingleOrDefaultAsync(w => w.Id == id, ct);
        if (order is null)
        {
            return Results.NotFound();
        }

        // A time with no zone is UTC, as everywhere else; one with an offset is converted.
        var started = request.StartedAtUtc.Kind == DateTimeKind.Local
            ? request.StartedAtUtc.ToUniversalTime()
            : DateTime.SpecifyKind(request.StartedAtUtc, DateTimeKind.Utc);
        var now = clock.GetUtcNow().UtcDateTime;

        if (ResponseRules.StartedTimeProblem(order.Status, order.ReportedAtUtc, order.ResolvedAtUtc, started, now, request.Reason) is { } problem)
        {
            // A fault that is not solved is the caller asking for the wrong thing: a conflict, not bad input.
            return order.Status is WorkOrderStatus.Resolved or WorkOrderStatus.Closed
                ? Results.BadRequest(new { error = problem })
                : Results.Conflict(new { error = problem });
        }

        var zone = ReportTime.Zone(hospital.Offset);
        var was = order.StartedAtUtc is { } old
            ? $"{ReportTime.DateTime(old, hospital.Offset)} {zone}"
            : "not recorded";

        order.StartedAtUtc = started;
        order.UpdatedAtUtc = now;
        db.WorkOrderNotes.Add(new WorkOrderNote
        {
            WorkOrderId = order.Id,
            Body = $"Started time corrected from {was} to {ReportTime.DateTime(started, hospital.Offset)} {zone}. Reason: {request.Reason!.Trim()}",
            AuthorUserId = author,
            CreatedAtUtc = now,
        });

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>The target for every priority: what is saved, and the starting value for one that has no row.</summary>
    public static async Task<Dictionary<WorkOrderPriority, int>> LoadAsync(HospitalPmDbContext db, CancellationToken ct)
    {
        var saved = await db.ResponseTargets.AsNoTracking().ToDictionaryAsync(t => t.Priority, t => t.Minutes, ct);
        return ResponseRules.Defaults.ToDictionary(d => d.Key, d => saved.TryGetValue(d.Key, out var m) ? m : d.Value);
    }

    private static async Task<IResult> ListAsync(HospitalPmDbContext db, CancellationToken ct)
    {
        var targets = await LoadAsync(db, ct);

        // Most urgent first, as people think of them.
        return Results.Ok(new
        {
            minMinutes = ResponseRules.MinMinutes,
            maxMinutes = ResponseRules.MaxMinutes,
            targets = targets.OrderByDescending(t => t.Key).Select(t => new { priority = t.Key, minutes = t.Value }),
        });
    }

    private static async Task<IResult> SaveAsync(
        [FromBody] ResponseTargetsRequest request,
        HospitalPmDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (request.Targets is not { Count: > 0 } wanted)
        {
            return Results.BadRequest(new { error = "Send at least one priority and its time." });
        }

        foreach (var w in wanted)
        {
            if (!ResponseRules.Defaults.ContainsKey(w.Priority))
            {
                return Results.BadRequest(new { error = "Unknown priority." });
            }

            if (ResponseRules.Problem(w.Minutes) is { } problem)
            {
                return Results.BadRequest(new { error = problem });
            }
        }

        if (wanted.Select(w => w.Priority).Distinct().Count() != wanted.Count)
        {
            return Results.BadRequest(new { error = "Each priority can be given once." });
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var existing = await db.ResponseTargets.ToDictionaryAsync(t => t.Priority, ct);

        foreach (var w in wanted)
        {
            if (existing.TryGetValue(w.Priority, out var row))
            {
                if (row.Minutes != w.Minutes)
                {
                    row.Minutes = w.Minutes;
                    row.UpdatedAtUtc = now;
                }
            }
            else
            {
                db.ResponseTargets.Add(new ResponseTarget { Priority = w.Priority, Minutes = w.Minutes, UpdatedAtUtc = now });
            }
        }

        await db.SaveChangesAsync(ct);
        return await ListAsync(db, ct);
    }
}
