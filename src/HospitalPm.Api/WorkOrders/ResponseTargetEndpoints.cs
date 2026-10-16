using HospitalPm.Api.Auth;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.WorkOrders;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.WorkOrders;

public sealed record ResponseTargetRequest(WorkOrderPriority Priority, int Minutes);

public sealed record ResponseTargetsRequest(List<ResponseTargetRequest>? Targets);

/// <summary>
/// How quickly an engineer is expected to respond to a fault, by priority: from the fault being raised to the engineer
/// pressing In progress. Everyone who can see work orders sees the targets (each fault is judged against them); only the
/// people who assign work change them.
/// </summary>
public static class ResponseTargetEndpoints
{
    public static void MapResponseTargetEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/work-orders/response-targets")
            .WithTags("Work orders")
            .RequirePermission(Permissions.WorkOrdersView);

        group.MapGet("/", ListAsync);
        group.MapPut("/", SaveAsync).RequirePermission(Permissions.WorkOrdersAssign);
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
