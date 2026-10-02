using System.Security.Claims;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace HospitalPm.Api.Auth;

public sealed record LoginRequest(string UserName, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record TokenResponse(string AccessToken, string RefreshToken, DateTime ExpiresAtUtc);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/refresh", RefreshAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).RequireAuthorization();
        group.MapGet("/me", Me).RequireAuthorization();
    }

    private static async Task<IResult> LoginAsync(
        [FromBody] LoginRequest request,
        UserManager<ApplicationUser> users,
        SignInManager<ApplicationUser> signIn,
        TokenService tokens,
        TimeProvider clock,
        CancellationToken ct)
    {
        var user = await users.FindByNameAsync(request.UserName);

        // Identical response whether the user does not exist, is deactivated,
        // or the password is wrong. Distinguishing them turns the login form
        // into a way to enumerate valid staff accounts.
        if (user is null || !user.IsActive)
        {
            return Results.Unauthorized();
        }

        // lockoutOnFailure: repeated wrong passwords lock the account rather
        // than allowing unlimited offline-speed guessing against a hospital
        // LAN where the service is reachable by everyone.
        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

        if (!result.Succeeded)
        {
            return Results.Unauthorized();
        }

        user.LastLoginAtUtc = clock.GetUtcNow().UtcDateTime;
        await users.UpdateAsync(user);

        var pair = await tokens.IssueAsync(user, ct);

        return Results.Ok(new TokenResponse(pair.AccessToken, pair.RefreshToken, pair.AccessExpiresAtUtc));
    }

    private static async Task<IResult> RefreshAsync(
        [FromBody] RefreshRequest request,
        TokenService tokens,
        CancellationToken ct)
    {
        var pair = await tokens.RefreshAsync(request.RefreshToken, ct);

        return pair is null
            ? Results.Unauthorized()
            : Results.Ok(new TokenResponse(pair.AccessToken, pair.RefreshToken, pair.AccessExpiresAtUtc));
    }

    private static async Task<IResult> LogoutAsync(
        [FromBody] RefreshRequest request,
        TokenService tokens,
        CancellationToken ct)
    {
        await tokens.RevokeAsync(request.RefreshToken, ct);
        return Results.NoContent();
    }

    private static IResult Me(ClaimsPrincipal principal) => Results.Ok(new
    {
        userName = principal.Identity?.Name,
        fullName = principal.FindFirstValue("full_name"),
        tenantId = principal.FindFirstValue("tenant_id"),
        roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray(),
        // What the person may do, so the screen shows what the server will allow and no more.
        permissions = RolePermissions
            .For(principal.FindAll(ClaimTypes.Role).Select(c => c.Value))
            .Order(StringComparer.Ordinal)
            .ToArray(),
    });
}
