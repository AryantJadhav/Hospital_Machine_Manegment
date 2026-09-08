using System.Security.Claims;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Identity;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Auth;

public sealed record CreateUserRequest(
    string UserName,
    string FullName,
    string? StaffCode,
    string Role,
    string Password);

public sealed record UpdateUserRequest(string FullName, string? StaffCode, string Role);

public sealed record ResetPasswordRequest(string Password);

public sealed record UserResponse(
    int Id,
    string UserName,
    string FullName,
    string? StaffCode,
    string Role,
    bool IsActive,
    DateTime? LastLoginAtUtc);

/// <summary>
/// The hospital's staff.
///
/// Until this existed an installation had exactly one account, forever:
/// /api/setup/first-admin creates the first administrator and then refuses,
/// telling the caller to "ask an administrator to create your account" — and
/// no endpoint existed that an administrator could use to do that.
///
/// Everything downstream depended on it and quietly did not work. Roles were
/// untestable, a PM signature named the only account in the system, the audit
/// trail's "who did what" had one answer, and the work-order screen assigned
/// every job to user id 1 because there was nobody else.
///
/// A biomedical department is a team: someone who plans and people who walk
/// the rounds. This is what lets it be one.
/// </summary>
public static class UserEndpoints
{
    public static void MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        // The whole group is administrators only, listing included.
        //
        // Reading the list used to be open to everyone, because the
        // work-order screen needs real people to offer in the assign
        // dropdown. Assigning is now an Admin act, so that reason is gone,
        // and what was left was every technician's browser downloading each
        // colleague's role, staff code and last sign-in. Nothing an Employee
        // can see asks for it.
        var group = app.MapGroup("/api/users")
            .WithTags("Staff")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        group.MapGet("/", ListAsync);

        group.MapPost("/", CreateAsync);
        group.MapPut("/{id:int}", UpdateAsync);
        group.MapPost("/{id:int}/deactivate", DeactivateAsync);
        group.MapPost("/{id:int}/activate", ActivateAsync);
        group.MapPost("/{id:int}/reset-password", ResetPasswordAsync);

        // Deliberately no delete. A user is referenced by every PM they
        // signed, every work order they touched and every audit row they
        // caused, all of which must stay readable for years. Deactivating is
        // the same reasoning as condemning a machine rather than erasing it.
    }

    private static async Task<IResult> ListAsync(
        HospitalPmDbContext db, [FromQuery] bool? includeInactive, CancellationToken ct)
    {
        var query = db.Users.AsNoTracking();

        if (includeInactive != true)
        {
            query = query.Where(u => u.IsActive);
        }

        // The role comes from Identity's join tables rather than a column, so
        // it is looked up per user. A hospital has tens of staff, not
        // thousands, and this keeps the roles in one place rather than
        // duplicating them onto the user row where they could drift.
        var users = await query
            .OrderBy(u => u.FullName)
            .Select(u => new
            {
                u.Id,
                u.UserName,
                u.FullName,
                u.StaffCode,
                u.IsActive,
                u.LastLoginAtUtc,
                Roles = db.UserRoles
                    .Where(ur => ur.UserId == u.Id)
                    .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name)
                    .ToList(),
            })
            .ToListAsync(ct);

        return Results.Ok(users.Select(u => new UserResponse(
            u.Id,
            u.UserName ?? string.Empty,
            u.FullName,
            u.StaffCode,
            u.Roles.FirstOrDefault() ?? string.Empty,
            u.IsActive,
            u.LastLoginAtUtc)));
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateUserRequest request,
        UserManager<ApplicationUser> users,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.FullName))
        {
            return Results.BadRequest(new { error = "Username and full name are required." });
        }

        if (!Roles.All.Contains(request.Role, StringComparer.Ordinal))
        {
            return Results.BadRequest(new
            {
                error = $"Unknown role. Use one of: {string.Join(", ", Roles.All)}.",
            });
        }

        var user = new ApplicationUser
        {
            UserName = request.UserName.Trim(),
            FullName = request.FullName.Trim(),
            StaffCode = string.IsNullOrWhiteSpace(request.StaffCode) ? null : request.StaffCode.Trim(),
            IsActive = true,
        };

        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            // Surfaced verbatim. An administrator setting up staff needs to
            // know the password rule, and guessing at it is how they end up
            // choosing something weak that happens to pass.
            return Results.BadRequest(new
            {
                error = string.Join(" ", created.Errors.Select(e => e.Description)),
            });
        }

        var roled = await users.AddToRoleAsync(user, request.Role);
        if (!roled.Succeeded)
        {
            // An account with no role can sign in and do nothing, which looks
            // like a broken product rather than a failed request. Remove it
            // and let the administrator try again.
            await users.DeleteAsync(user);
            return Results.Problem("The account could not be given its role, so it was not created.");
        }

        return Results.Created($"/api/users/{user.Id}", new { user.Id });
    }

    private static async Task<IResult> UpdateAsync(
        int id,
        [FromBody] UpdateUserRequest request,
        UserManager<ApplicationUser> users,
        HospitalPmDbContext db,
        ClaimsPrincipal caller,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return Results.NotFound();
        }

        if (!Roles.All.Contains(request.Role, StringComparer.Ordinal))
        {
            return Results.BadRequest(new { error = "Unknown role." });
        }

        var current = (await users.GetRolesAsync(user)).FirstOrDefault();

        if (current == Roles.Admin && request.Role != Roles.Admin
            && await LastAdminAsync(db, id, ct))
        {
            return Results.BadRequest(new
            {
                error = "This is the only administrator. Give someone else the Admin role first, "
                        + "or this installation would have nobody who can manage it.",
            });
        }

        user.FullName = request.FullName.Trim();
        user.StaffCode = string.IsNullOrWhiteSpace(request.StaffCode) ? null : request.StaffCode.Trim();
        await users.UpdateAsync(user);

        if (current != request.Role)
        {
            if (current is not null)
            {
                await users.RemoveFromRoleAsync(user, current);
            }
            await users.AddToRoleAsync(user, request.Role);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Stops someone signing in, without erasing what they did.
    ///
    /// Their refresh tokens are revoked at the same time. Login and refresh
    /// both already check IsActive, so this closes the only remaining window —
    /// an access token already issued, which expires within fifteen minutes.
    /// </summary>
    private static async Task<IResult> DeactivateAsync(
        int id,
        UserManager<ApplicationUser> users,
        HospitalPmDbContext db,
        ClaimsPrincipal caller,
        TimeProvider clock,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return Results.NotFound();
        }

        // Locking yourself out of a hospital PC that has no password-reset
        // path and may not be on a network is not recoverable by anyone there.
        if (CallerId(caller) == id)
        {
            return Results.BadRequest(new { error = "You cannot deactivate your own account." });
        }

        if (await LastAdminAsync(db, id, ct))
        {
            return Results.BadRequest(new
            {
                error = "This is the only active administrator. Nobody would be able to manage "
                        + "this installation afterwards.",
            });
        }

        user.IsActive = false;
        await users.UpdateAsync(user);

        var now = clock.GetUtcNow().UtcDateTime;
        await db.RefreshTokens
            .Where(t => t.UserId == id && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAtUtc, now)
                .SetProperty(t => t.RevokedReason, "user deactivated"), ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ActivateAsync(
        int id, UserManager<ApplicationUser> users, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return Results.NotFound();
        }

        user.IsActive = true;
        await users.UpdateAsync(user);

        return Results.NoContent();
    }

    /// <summary>
    /// Sets a new password for someone who has forgotten theirs.
    ///
    /// An administrator sets it directly rather than emailing a link: this
    /// install has no internet and often no mail server, and a hospital's
    /// answer to a forgotten password is to walk to the biomedical office.
    /// </summary>
    private static async Task<IResult> ResetPasswordAsync(
        int id,
        [FromBody] ResetPasswordRequest request,
        UserManager<ApplicationUser> users,
        HospitalPmDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return Results.NotFound();
        }

        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, request.Password);

        if (!result.Succeeded)
        {
            return Results.BadRequest(new
            {
                error = string.Join(" ", result.Errors.Select(e => e.Description)),
            });
        }

        // Every session they had ends. A password is reset because it may be
        // known to someone else, and leaving their existing sessions alive
        // would defeat the point.
        var now = clock.GetUtcNow().UtcDateTime;
        await db.RefreshTokens
            .Where(t => t.UserId == id && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAtUtc, now)
                .SetProperty(t => t.RevokedReason, "password reset"), ct);

        return Results.NoContent();
    }

    private static int? CallerId(ClaimsPrincipal caller)
        => int.TryParse(caller.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>
    /// True when <paramref name="id"/> is the only active administrator left.
    /// </summary>
    private static async Task<bool> LastAdminAsync(
        HospitalPmDbContext db, int id, CancellationToken ct)
    {
        var adminRoleId = await db.Roles
            .Where(r => r.Name == Roles.Admin)
            .Select(r => r.Id)
            .SingleOrDefaultAsync(ct);

        if (adminRoleId == 0)
        {
            return false;
        }

        var otherActiveAdmins = await db.UserRoles
            .Where(ur => ur.RoleId == adminRoleId && ur.UserId != id)
            .Join(db.Users, ur => ur.UserId, u => u.Id, (_, u) => u)
            .CountAsync(u => u.IsActive, ct);

        return otherActiveAdmins == 0;
    }
}
