using System.Globalization;
using System.Security.Claims;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Locations;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Equipment;

public sealed record MoveRequest(int ToLocationId, string? Reason);

public static class EquipmentMoveEndpoints
{
    /// <summary>The list on a machine's page. A machine shifted daily for years has a long one.</summary>
    private const int MaxRows = 100;

    public static void MapEquipmentMoveEndpoints(this IEndpointRouteBuilder app)
    {
        // Open to every signed-in user, not just admins: shifting a machine to
        // another room is a day-to-day act of the engineer who is standing there,
        // and the register being wrong until an admin fixes it is how a technician
        // ends up in the wrong room.
        var group = app.MapGroup("/api/equipment/{id:int}").WithTags("Equipment").RequireAuthorization();

        group.MapGet("/moves", MovesAsync);
        group.MapPost("/move", MoveAsync);
    }

    /// <summary>Notes a change of place. Shared with the edit form, which can change the place too.</summary>
    public static void Record(
        HospitalPmDbContext db, Domain.Assets.Equipment machine, int toLocationId,
        string? reason, int userId, DateTime nowUtc)
    {
        db.EquipmentMoves.Add(new EquipmentMove
        {
            TenantId = machine.TenantId,
            EquipmentId = machine.Id,
            FromLocationId = machine.LocationId,
            ToLocationId = toLocationId,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            MovedByUserId = userId,
            MovedAtUtc = nowUtc,
        });
    }

    public static int UserId(ClaimsPrincipal principal) => int.Parse(
        principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
        ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "0",
        CultureInfo.InvariantCulture);

    private static async Task<IResult> MovesAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        if (!await db.Equipment.AnyAsync(e => e.Id == id, ct))
        {
            return Results.NotFound();
        }

        var rows = await db.EquipmentMoves.AsNoTracking()
            .Where(m => m.EquipmentId == id)
            .OrderByDescending(m => m.MovedAtUtc).ThenByDescending(m => m.Id)
            .Take(MaxRows)
            .Select(m => new
            {
                m.Id,
                m.MovedAtUtc,
                m.Reason,
                from = m.FromLocationId == null
                    ? null
                    : db.Locations.Where(l => l.Id == m.FromLocationId).Select(l => l.Name).FirstOrDefault(),
                to = db.Locations.Where(l => l.Id == m.ToLocationId).Select(l => l.Name).FirstOrDefault(),
                movedBy = db.Users.Where(u => u.Id == m.MovedByUserId).Select(u => u.FullName).FirstOrDefault(),
            })
            .ToListAsync(ct);

        return Results.Ok(rows);
    }

    private static async Task<IResult> MoveAsync(
        int id,
        [FromBody] MoveRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        var machine = await db.Equipment.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (machine is null)
        {
            return Results.NotFound();
        }

        if (machine.Status is EquipmentStatus.Condemned or EquipmentStatus.Disposed)
        {
            return Results.Conflict(new { error = "A condemned or disposed machine is not moved." });
        }

        if (request.ToLocationId == machine.LocationId)
        {
            return Results.Conflict(new { error = "The machine is already there." });
        }

        var level = await db.Locations
            .Where(l => l.Id == request.ToLocationId)
            .Select(l => (int?)l.Level)
            .SingleOrDefaultAsync(ct);

        if (level is null)
        {
            return Results.BadRequest(new { error = "Unknown place." });
        }

        // The same rule as the register: a machine sits in a building or deeper.
        if (level < (int)LocationLevel.Building)
        {
            return Results.BadRequest(new
            {
                error = "A machine goes in a building, floor, department or room, not an organisation or site.",
            });
        }

        if (request.Reason is { Length: > 500 })
        {
            return Results.BadRequest(new { error = "The reason is too long." });
        }

        Record(db, machine, request.ToLocationId, request.Reason, UserId(principal), clock.GetUtcNow().UtcDateTime);
        machine.LocationId = request.ToLocationId;

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
