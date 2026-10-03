using System.Security.Claims;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Auth;

/// <summary>Asks that the signed-in person holds one permission. See <see cref="Permissions"/>.</summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// What one person may do right now: their role's permissions, plus any section the Developer has
/// given them, minus any taken away (see <see cref="EffectivePermissions"/>).
///
/// Worked out from the database on each request, not copied into the sign-in token, so a change
/// takes effect on the person's very next click and a section taken away is gone at once rather than
/// when their token runs out. A person's few grants are read once per request.
/// </summary>
public sealed class PermissionService(HospitalPmDbContext db, HospitalClock clock)
{
    private int? _forUser;
    private IReadOnlySet<string>? _cached;

    public static int? UserIdOf(ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? user.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
        return int.TryParse(raw, out var id) ? id : null;
    }

    public async Task<IReadOnlySet<string>> ForAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var id = UserIdOf(user);
        if (_cached is not null && _forUser == id)
        {
            return _cached;
        }

        var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();

        // A Developer holds everything whatever is written, so there is nothing to look up.
        List<PermissionGrant> grants = id is null || roles.Contains(Roles.Developer)
            ? []
            : await db.PermissionGrants.AsNoTracking().Where(g => g.UserId == id).ToListAsync(ct);

        _forUser = id;
        _cached = EffectivePermissions.For(roles, grants, clock.Today());
        return _cached;
    }

    public async Task<bool> CanAsync(ClaimsPrincipal user, string permission, CancellationToken ct = default)
        => (await ForAsync(user, ct)).Contains(permission);
}

/// <summary>
/// Decides a <see cref="PermissionRequirement"/> through <see cref="PermissionService"/>. This is the
/// only place a permission is turned into an answer for a request.
/// </summary>
public sealed class PermissionHandler(PermissionService permissions) : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (await permissions.CanAsync(context.User, requirement.Permission))
        {
            context.Succeed(requirement);
        }
    }
}

public static class PermissionExtensions
{
    /// <summary>
    /// Only people who hold the permission. Not signed in is a 401, signed in without it a 403,
    /// the same as <c>RequireRole</c> was.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(p => p
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(permission)));
}
