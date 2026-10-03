using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Auth;

/// <summary>
/// Limits a person in another department to their own departments.
///
/// A person who may see the equipment of their own departments (<see cref="Permissions.DepartmentView"/>)
/// but not the whole register (<see cref="Permissions.RegisterView"/>) has the context restricted to the
/// places they have been given, and everything beneath them, before any endpoint reads anything. Equipment,
/// service requests and locations outside those places are then simply not there for that request, so no
/// endpoint can show them by forgetting to ask.
///
/// Someone who has been given no department is shown nothing: access not yet set up is closed.
/// </summary>
public sealed class DepartmentScopeMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, PermissionService permissions, HospitalPmDbContext db)
    {
        if (http.User.Identity?.IsAuthenticated == true)
        {
            var held = await permissions.ForAsync(http.User, http.RequestAborted);

            if (held.Contains(Permissions.DepartmentView) && !held.Contains(Permissions.RegisterView))
            {
                db.RestrictTo(await PlacesOfAsync(db, PermissionService.UserIdOf(http.User), http.RequestAborted));
            }
        }

        await next(http);
    }

    /// <summary>The places this person was given, and every place beneath them.</summary>
    public static async Task<List<int>> PlacesOfAsync(HospitalPmDbContext db, int? userId, CancellationToken ct)
    {
        if (userId is null)
        {
            return [];
        }

        var homes = await (
            from ul in db.UserLocations.AsNoTracking()
            where ul.UserId == userId
            join l in db.Locations.AsNoTracking() on ul.LocationId equals l.Id
            select l.Path).ToListAsync(ct);

        if (homes.Count == 0)
        {
            return [];
        }

        // A hospital has hundreds of places, not thousands, so the tree is read once and walked here
        // rather than asking the database for a prefix match against each of the person's places.
        var all = await db.Locations.AsNoTracking().Select(l => new { l.Id, l.Path }).ToListAsync(ct);

        return all.Where(l => homes.Any(h => l.Path.StartsWith(h, StringComparison.Ordinal))).Select(l => l.Id).ToList();
    }
}
