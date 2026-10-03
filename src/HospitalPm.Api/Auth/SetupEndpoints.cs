using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Identity;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Auth;

public sealed record CreateFirstAdminRequest(string UserName, string FullName, string Password);

/// <summary>
/// First-run bootstrap: creates the first account, a Developer.
///
/// Anonymous, but only while the user table is empty. The moment one
/// account exists this returns 409 forever, so the window is the seconds
/// between install and the operator setting up. This is the same shape as
/// the initial-setup screens shipped by most self-hosted software.
///
/// The alternative — seeding a default admin with a known password in a
/// migration — would put identical working credentials on every hospital
/// install in the country, including the ones nobody remembers to change.
///
/// Phase 2's installer wizard replaces the UI for this, but the endpoint
/// stays: a silent install still needs a way to create the first account.
/// </summary>
public static class SetupEndpoints
{
    public static void MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/setup").WithTags("Setup");

        group.MapGet("/status", StatusAsync).AllowAnonymous();
        group.MapPost("/first-admin", CreateFirstAdminAsync).AllowAnonymous();
    }

    /// <summary>
    /// Lets the UI decide between the login form and the setup form.
    /// Leaks only whether the install has been configured, which an
    /// unconfigured install reveals anyway by accepting the next call.
    /// </summary>
    private static async Task<IResult> StatusAsync(HospitalPmDbContext db, CancellationToken ct)
        => Results.Ok(new { needsSetup = !await db.Users.AnyAsync(ct) });

    private static async Task<IResult> CreateFirstAdminAsync(
        [FromBody] CreateFirstAdminRequest request,
        HospitalPmDbContext db,
        UserManager<ApplicationUser> users,
        CancellationToken ct)
    {
        // Checked inside a serializable transaction so two simultaneous
        // requests cannot both see an empty table and both create an admin.
        await using var tx = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);

        if (await db.Users.AnyAsync(ct))
        {
            return Results.Conflict(new
            {
                error = "This installation is already set up. Sign in, or ask an administrator to create your account.",
            });
        }

        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.FullName))
        {
            return Results.BadRequest(new { error = "Username and full name are required." });
        }

        var user = new ApplicationUser
        {
            UserName = request.UserName.Trim(),
            FullName = request.FullName.Trim(),
            IsActive = true,
        };

        var created = await users.CreateAsync(user, request.Password);
        if (!created.Succeeded)
        {
            // Password rules are surfaced verbatim. There is no account to
            // enumerate yet, so being specific costs nothing and saves the
            // operator guessing at the policy.
            return Results.BadRequest(new
            {
                error = string.Join(" ", created.Errors.Select(e => e.Description)),
            });
        }

        var roled = await users.AddToRoleAsync(user, Roles.Developer);
        if (!roled.Succeeded)
        {
            // An admin with no role can sign in but do nothing, and the
            // endpoint that would fix it is now closed. Fail the whole thing
            // instead and let setup be retried.
            return Results.Problem("The account was created but its role could not be assigned.");
        }

        await tx.CommitAsync(ct);

        return Results.Created($"/api/users/{user.Id}", new { user.Id, user.UserName });
    }
}
