using System.Security.Claims;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Identity;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Auth;

/// <summary>One section given to a person or taken from them. Give <see cref="ExpiresOn"/> for a loan that ends.</summary>
public sealed record AccessGrantRequest(string? Permission, string? Effect, DateOnly? ExpiresOn, string? Note);

/// <summary>The whole set for one person. It replaces what was there, so a section left out is taken back to the role's.</summary>
public sealed record SetAccessRequest(List<AccessGrantRequest>? Grants);

/// <summary>
/// Giving one person access to a section their role does not give, or taking one away.
///
/// Only the Developer does this (the <see cref="Permissions.AccessManage"/> permission is held by no
/// one else, and cannot be given). The hospital is never kept in the dark about it: the Head of
/// Biomedical and the IT team can read what each person has been given (on the Staff page), and every
/// change is in the audit log, written by the database.
/// </summary>
public static class AccessEndpoints
{
    private const int MaxNoteLength = 500;
    private const int MaxYearsAhead = 5;

    public static void MapAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/access").WithTags("Access");

        // Names and groups of what can be given. Nothing secret in it, and the Staff page needs it too.
        group.MapGet("/catalog", () => Results.Ok(PermissionCatalog.All)).RequireAuthorization();

        group.MapGet("/users/{id:int}", GetAsync).RequirePermission(Permissions.AccessManage);
        group.MapPut("/users/{id:int}", SetAsync).RequirePermission(Permissions.AccessManage);
    }

    /// <summary>The person's role, what it gives, what has been given or taken on top, and the sum.</summary>
    public static async Task<IResult> ViewAsync(
        int id, UserManager<ApplicationUser> users, HospitalPmDbContext db, HospitalClock clock, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return Results.NotFound();
        }

        var role = (await users.GetRolesAsync(user)).FirstOrDefault();
        return Results.Ok(await BuildViewAsync(user, role, db, clock, ct));
    }

    private static Task<IResult> GetAsync(
        int id, UserManager<ApplicationUser> users, HospitalPmDbContext db, HospitalClock clock, CancellationToken ct)
        => ViewAsync(id, users, db, clock, ct);

    private static async Task<object> BuildViewAsync(
        ApplicationUser user, string? role, HospitalPmDbContext db, HospitalClock clock, CancellationToken ct)
    {
        var today = clock.Today();

        var rows = await db.PermissionGrants.AsNoTracking()
            .Where(g => g.UserId == user.Id)
            .OrderBy(g => g.Permission)
            .ToListAsync(ct);

        var byId = await db.Users.AsNoTracking()
            .Where(u => rows.Select(g => g.GrantedByUserId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        var roles = role is null ? [] : new List<string> { role };

        return new
        {
            user = new { user.Id, user.UserName, user.FullName, role, user.IsActive },
            rolePermissions = RolePermissions.For(role).Order(StringComparer.Ordinal).ToArray(),
            grants = rows.Select(g => new
            {
                g.Permission,
                g.Effect,
                g.ExpiresOn,
                g.Note,
                // A grant that has run out is shown, as having run out, until it is replaced.
                Expired = !EffectivePermissions.IsActive(g, today),
                GrantedByName = byId.GetValueOrDefault(g.GrantedByUserId),
                g.GrantedAtUtc,
            }),
            effective = EffectivePermissions.For(roles, rows, today).Order(StringComparer.Ordinal).ToArray(),
            // Why this person's access cannot be changed here, or null when it can.
            locked = LockedReason(role),
        };
    }

    private static string? LockedReason(string? role) => role switch
    {
        Roles.Developer => "A Developer holds everything, so there is nothing to give or take away.",
        // Their view is limited to their own departments, and until that limit is in place, giving
        // one a section would show them every department's.
        Roles.DepartmentUser => "A department user cannot be given extra access yet: it would not be limited to their own departments.",
        _ => null,
    };

    private static async Task<IResult> SetAsync(
        int id,
        [FromBody] SetAccessRequest request,
        UserManager<ApplicationUser> users,
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal caller,
        CancellationToken ct)
    {
        var user = await users.FindByIdAsync(id.ToString());
        if (user is null)
        {
            return Results.NotFound();
        }

        var role = (await users.GetRolesAsync(user)).FirstOrDefault();
        if (LockedReason(role) is { } locked)
        {
            return Results.BadRequest(new { error = locked });
        }

        var today = clock.Today();
        var held = RolePermissions.For(role);
        var wanted = new List<PermissionGrant>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var g in request.Grants ?? [])
        {
            var label = PermissionCatalog.All.FirstOrDefault(p => p.Permission == g.Permission)?.Label;
            if (label is null || g.Permission is null)
            {
                return Results.BadRequest(new { error = $"\"{g.Permission}\" is not a section that can be given." });
            }

            if (!seen.Add(g.Permission))
            {
                return Results.BadRequest(new { error = $"\"{label}\" is listed twice." });
            }

            if (!GrantEffect.IsKnown(g.Effect))
            {
                return Results.BadRequest(new { error = $"\"{label}\": say whether it is given or taken away." });
            }

            // Saying what is already so would leave a row that means nothing and a list that cannot be read.
            if (g.Effect == GrantEffect.Grant && held.Contains(g.Permission))
            {
                return Results.BadRequest(new { error = $"\"{label}\" is already part of their role." });
            }

            if (g.Effect == GrantEffect.Revoke && !held.Contains(g.Permission))
            {
                return Results.BadRequest(new { error = $"\"{label}\" is not part of their role, so there is nothing to take away." });
            }

            if (g.ExpiresOn is { } end && (end < today || end > today.AddYears(MaxYearsAhead)))
            {
                return Results.BadRequest(new { error = $"\"{label}\": the end date must be from today to {MaxYearsAhead} years away." });
            }

            var note = string.IsNullOrWhiteSpace(g.Note) ? null : g.Note.Trim();
            if (note?.Length > MaxNoteLength)
            {
                return Results.BadRequest(new { error = $"A note can be at most {MaxNoteLength} characters." });
            }

            wanted.Add(new PermissionGrant
            {
                UserId = id,
                Permission = g.Permission,
                Effect = g.Effect!,
                ExpiresOn = g.ExpiresOn,
                Note = note,
                GrantedByUserId = PermissionService.UserIdOf(caller) ?? 0,
                GrantedAtUtc = DateTime.UtcNow,
            });
        }

        var existing = await db.PermissionGrants.Where(g => g.UserId == id).ToListAsync(ct);

        // A row that is the same as before is left alone, so who gave it and when stays true.
        foreach (var old in existing)
        {
            var now = wanted.FirstOrDefault(w => w.Permission == old.Permission);
            if (now is null)
            {
                db.PermissionGrants.Remove(old);
            }
            else if (now.Effect != old.Effect || now.ExpiresOn != old.ExpiresOn || now.Note != old.Note)
            {
                old.Effect = now.Effect;
                old.ExpiresOn = now.ExpiresOn;
                old.Note = now.Note;
                old.GrantedByUserId = now.GrantedByUserId;
                old.GrantedAtUtc = now.GrantedAtUtc;
            }

            wanted.RemoveAll(w => w.Permission == old.Permission);
        }

        db.PermissionGrants.AddRange(wanted);
        await db.SaveChangesAsync(ct);

        return Results.Ok(await BuildViewAsync(user, role, db, clock, ct));
    }
}
