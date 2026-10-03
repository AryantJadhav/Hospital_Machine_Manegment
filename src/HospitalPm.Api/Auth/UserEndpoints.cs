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
    DateTime? LastLoginAtUtc,
    // Sections given on top of the role, and taken away from it, that are still in force.
    int AccessGiven,
    int AccessTakenAway,
    // For a department user: how many departments they have been given. Nothing is shown until it is at least one.
    int Departments);

/// <summary>The places a department user belongs to. It replaces what was there.</summary>
public sealed record SetDepartmentsRequest(List<int>? LocationIds);

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
            .RequirePermission(Permissions.StaffManage);

        group.MapGet("/", ListAsync);

        // What one person has been given or had taken away, for the hospital to read. Changing it is
        // the Developer's, on the Access page.
        group.MapGet("/{id:int}/access", AccessEndpoints.ViewAsync);

        // The departments a department user may see. Whoever may manage the account sets them.
        group.MapGet("/{id:int}/departments", GetDepartmentsAsync);
        group.MapPut("/{id:int}/departments", SetDepartmentsAsync);

        group.MapPost("/", CreateAsync);
        group.MapPut("/{id:int}", UpdateAsync);
        group.MapPost("/{id:int}/deactivate", DeactivateAsync);
        group.MapPost("/{id:int}/activate", ActivateAsync);
        group.MapPost("/{id:int}/reset-password", ResetPasswordAsync);

        // Names only, for the places that need to pick a person: who a service request is assigned to, who
        // attended a training session, whose work a report is about. Not the staff list: no roles, no staff
        // codes, no sign-in times. Open to whoever holds one of the permissions that needs it, so that giving
        // someone "assign work" does not also give them the whole staff list.
        app.MapGet("/api/people", PeopleAsync).WithTags("Staff").RequireAuthorization();

        // Deliberately no delete. A user is referenced by every PM they
        // signed, every work order they touched and every audit row they
        // caused, all of which must stay readable for years. Deactivating is
        // the same reasoning as condemning a machine rather than erasing it.
    }

    private static async Task<IResult> PeopleAsync(
        HospitalPmDbContext db, ClaimsPrincipal caller, PermissionService permissions, CancellationToken ct)
    {
        var allowed = await permissions.ForAsync(caller, ct);
        if (!allowed.Contains(Permissions.WorkOrdersAssign)
            && !allowed.Contains(Permissions.TrainingEdit)
            && !allowed.Contains(Permissions.ReportsView)
            && !allowed.Contains(Permissions.StaffManage))
        {
            return Results.Forbid();
        }

        var people = await db.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.FullName)
            .Select(u => new { u.Id, u.FullName })
            .ToListAsync(ct);

        return Results.Ok(people);
    }

    private static async Task<IResult> ListAsync(
        HospitalPmDbContext db, HospitalPm.Infrastructure.Maintenance.HospitalClock clock,
        [FromQuery] bool? includeInactive, CancellationToken ct)
    {
        var today = clock.Today();
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
                Given = db.PermissionGrants.Count(g => g.UserId == u.Id && g.Effect == GrantEffect.Grant
                    && (g.ExpiresOn == null || g.ExpiresOn >= today)),
                Departments = db.UserLocations.Count(ul => ul.UserId == u.Id),
                TakenAway = db.PermissionGrants.Count(g => g.UserId == u.Id && g.Effect == GrantEffect.Revoke
                    && (g.ExpiresOn == null || g.ExpiresOn >= today)),
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
            u.LastLoginAtUtc,
            u.Given,
            u.TakenAway,
            u.Departments)));
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateUserRequest request,
        UserManager<ApplicationUser> users,
        ClaimsPrincipal caller,
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

        if (!MayManage(caller, request.Role))
        {
            return Forbidden($"You cannot create an account with the {Roles.Label(request.Role)} role.");
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

        // Nobody reaches above themselves: not to change an account of a higher kind, and not to give one.
        if (!MayManage(caller, current) || !MayManage(caller, request.Role))
        {
            return Forbidden("You cannot change that account or give it that role.");
        }

        if (CallerId(caller) == id && current != request.Role)
        {
            return Results.BadRequest(new { error = "You cannot change your own role." });
        }

        if (current != request.Role
            && RolePermissions.For(current).Contains(Permissions.StaffManage)
            && !RolePermissions.For(request.Role).Contains(Permissions.StaffManage)
            && await LastManagerAsync(db, id, ct))
        {
            return Results.BadRequest(new
            {
                error = "This is the only person who can manage staff. Give someone else a role that can "
                        + "first, or this installation would have nobody who can manage it.",
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

            // Departments are only for a department user. Someone moved to another role is not left
            // holding places that mean nothing, or that would mean something again if they moved back.
            if (current == Roles.DepartmentUser)
            {
                await db.UserLocations.Where(ul => ul.UserId == id).ExecuteDeleteAsync(ct);
            }
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

        if (!MayPause(caller, (await users.GetRolesAsync(user)).FirstOrDefault()))
        {
            return Forbidden("You cannot deactivate that account.");
        }

        if (await LastManagerAsync(db, id, ct))
        {
            return Results.BadRequest(new
            {
                error = "This is the only active person who can manage staff. Nobody would be able to manage "
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
        int id, UserManager<ApplicationUser> users, ClaimsPrincipal caller, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return Results.NotFound();
        }

        if (!MayPause(caller, (await users.GetRolesAsync(user)).FirstOrDefault()))
        {
            return Forbidden("You cannot activate that account.");
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
        ClaimsPrincipal caller,
        TimeProvider clock,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return Results.NotFound();
        }

        if (!MayManage(caller, (await users.GetRolesAsync(user)).FirstOrDefault()))
        {
            return Forbidden("You cannot reset the password of that account.");
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

    private static async Task<IResult> GetDepartmentsAsync(
        int id, UserManager<ApplicationUser> users, HospitalPmDbContext db, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return Results.NotFound();
        }

        var places = await (
            from ul in db.UserLocations.AsNoTracking()
            where ul.UserId == id
            join l in db.Locations.AsNoTracking() on ul.LocationId equals l.Id
            orderby l.Path
            select new { LocationId = l.Id, l.Name, l.Code, Level = (int)l.Level }).ToListAsync(ct);

        return Results.Ok(places);
    }

    private static async Task<IResult> SetDepartmentsAsync(
        int id,
        [FromBody] SetDepartmentsRequest request,
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

        var role = (await users.GetRolesAsync(user)).FirstOrDefault();
        if (!MayManage(caller, role))
        {
            return Forbidden("You cannot change the departments of that account.");
        }

        if (role != Roles.DepartmentUser)
        {
            return Results.BadRequest(new { error = "Only a department user has departments." });
        }

        var wanted = (request.LocationIds ?? []).Distinct().ToList();
        var known = await db.Locations.AsNoTracking().Where(l => wanted.Contains(l.Id)).Select(l => l.Id).ToListAsync(ct);
        if (known.Count != wanted.Count)
        {
            return Results.BadRequest(new { error = "One of the departments chosen does not exist." });
        }

        var existing = await db.UserLocations.Where(ul => ul.UserId == id).ToListAsync(ct);

        db.UserLocations.RemoveRange(existing.Where(e => !wanted.Contains(e.LocationId)));
        db.UserLocations.AddRange(wanted
            .Where(w => existing.All(e => e.LocationId != w))
            .Select(w => new UserLocation
            {
                UserId = id,
                LocationId = w,
                AssignedByUserId = CallerId(caller) ?? 0,
                AssignedAtUtc = DateTime.UtcNow,
            }));
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static IEnumerable<string> RolesOf(ClaimsPrincipal caller)
        => caller.FindAll(ClaimTypes.Role).Select(c => c.Value);

    /// <summary>May the caller create, change, or look after an account of this role?</summary>
    private static bool MayManage(ClaimsPrincipal caller, string? role)
        => role is not null && Roles.ManageableBy(RolesOf(caller)).Contains(role, StringComparer.Ordinal);

    /// <summary>May the caller stop this account signing in, or let it back in? See <see cref="Roles.PausableBy"/>.</summary>
    private static bool MayPause(ClaimsPrincipal caller, string? role)
        => role is not null && Roles.PausableBy(RolesOf(caller)).Contains(role, StringComparer.Ordinal);

    private static IResult Forbidden(string message)
        => Results.Json(new { error = message }, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// True when <paramref name="id"/> is the only active person left who can manage staff.
    /// </summary>
    private static async Task<bool> LastManagerAsync(
        HospitalPmDbContext db, int id, CancellationToken ct)
    {
        var managerRoleIds = await db.Roles
            .Where(r => r.Name != null && Roles.All.Contains(r.Name))
            .ToListAsync(ct);

        var ids = managerRoleIds
            .Where(r => RolePermissions.For(r.Name).Contains(Permissions.StaffManage))
            .Select(r => r.Id)
            .ToList();

        if (ids.Count == 0)
        {
            return false;
        }

        var otherActiveManagers = await db.UserRoles
            .Where(ur => ids.Contains(ur.RoleId) && ur.UserId != id)
            .Join(db.Users, ur => ur.UserId, u => u.Id, (_, u) => u)
            .CountAsync(u => u.IsActive, ct);

        return otherActiveManagers == 0;
    }
}
