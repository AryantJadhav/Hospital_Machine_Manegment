using System.Security.Claims;
using HospitalPm.Domain.Identity;
using Microsoft.AspNetCore.Authorization;

namespace HospitalPm.Api.Auth;

/// <summary>Asks that the signed-in person holds one permission. See <see cref="Permissions"/>.</summary>
public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

/// <summary>
/// Decides a <see cref="PermissionRequirement"/> from the person's roles, through the table in
/// <see cref="RolePermissions"/>. This is the only place a permission is turned into an answer, so
/// when access can also be granted to one person it changes here and nowhere else.
/// </summary>
public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.Can(requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

public static class PermissionExtensions
{
    /// <summary>Does this person hold the permission? For the few places that branch on it rather than refuse.</summary>
    public static bool Can(this ClaimsPrincipal user, string permission) =>
        RolePermissions.Has(user.FindAll(ClaimTypes.Role).Select(c => c.Value), permission);

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
