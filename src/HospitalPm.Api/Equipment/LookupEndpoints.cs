using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Equipment;

/// <summary>
/// Read-only reference data the UI needs to render filters and pickers.
///
/// Every authenticated role may read these: a technician filtering the
/// register by department needs the department list as much as an admin does.
/// </summary>
public static class LookupEndpoints
{
    public static void MapLookupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/lookups")
            .WithTags("Lookups")
            .RequireAuthorization();

        group.MapGet("/equipment-types", async (HospitalPmDbContext db, CancellationToken ct) =>
            Results.Ok(await db.EquipmentTypes
                .AsNoTracking()
                .Where(t => t.IsActive)
                .OrderBy(t => t.Name)
                .Select(t => new
                {
                    t.Id,
                    t.Code,
                    t.Name,
                    // Primary category drives the default grouping in the UI.
                    Category = t.Categories
                        .Where(c => c.IsPrimary)
                        .Select(c => c.Category!.Name)
                        .FirstOrDefault(),
                })
                .ToListAsync(ct)));

        group.MapGet("/categories", async (HospitalPmDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Categories
                .AsNoTracking()
                .Where(c => c.IsActive)
                .OrderBy(c => c.DisplayOrder)
                .Select(c => new { c.Id, c.Code, c.Name })
                .ToListAsync(ct)));

        // The whole tree in one call. A hospital has hundreds of locations,
        // not thousands, so paging it would add complexity the client would
        // only have to undo to render a tree.
        group.MapGet("/locations", async (HospitalPmDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Locations
                .AsNoTracking()
                .Where(l => l.IsActive)
                .OrderBy(l => l.Path)
                .Select(l => new
                {
                    l.Id,
                    l.Code,
                    l.Name,
                    l.ParentId,
                    Level = (int)l.Level,
                    l.Depth,
                })
                .ToListAsync(ct)));
    }
}
