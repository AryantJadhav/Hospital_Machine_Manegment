using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HospitalPm.Infrastructure.Identity;

public sealed record TokenPair(string AccessToken, string RefreshToken, DateTime AccessExpiresAtUtc);

public sealed class TokenService(
    HospitalPmDbContext db,
    UserManager<ApplicationUser> users,
    SigningKeyProvider keys,
    IOptions<JwtOptions> options,
    TimeProvider clock)
{
    private readonly JwtOptions _options = options.Value;

    public async Task<TokenPair> IssueAsync(ApplicationUser user, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var access = await CreateAccessTokenAsync(user, now);
        var refresh = await CreateRefreshTokenAsync(user, now, ct);

        return new TokenPair(access, refresh, now.AddMinutes(_options.AccessTokenMinutes));
    }

    /// <summary>
    /// Exchanges a refresh token for a new pair, rotating the old one.
    ///
    /// Reuse of an already-rotated token revokes the entire chain for that
    /// user. A legitimate client never replays a rotated token, so a replay
    /// means the token leaked and both copies must stop working.
    /// </summary>
    public async Task<TokenPair?> RefreshAsync(string? rawToken, CancellationToken ct = default)
    {
        // A request with no token is simply not a valid one. It must not throw: a server error here
        // would tell a stranger nothing useful and fill the log with noise.
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var hash = Hash(rawToken);

        var stored = await db.RefreshTokens
            .Include(t => t.User)
            .SingleOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null)
        {
            return null;
        }

        if (stored.RevokedAtUtc is not null)
        {
            await RevokeChainAsync(stored.UserId, now, "reuse detected", ct);
            return null;
        }

        if (stored.ExpiresAtUtc <= now || stored.User is null || !stored.User.IsActive)
        {
            return null;
        }

        var access = await CreateAccessTokenAsync(stored.User, now);
        var replacement = await CreateRefreshTokenAsync(stored.User, now, ct);

        stored.RevokedAtUtc = now;
        stored.RevokedReason = "rotated";
        await db.SaveChangesAsync(ct);

        return new TokenPair(access, replacement, now.AddMinutes(_options.AccessTokenMinutes));
    }

    public async Task RevokeAsync(string? rawToken, CancellationToken ct = default)
    {
        // Signing out with nothing to sign out of is already done.
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return;
        }

        var hash = Hash(rawToken);
        var stored = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);

        if (stored is null || stored.RevokedAtUtc is not null)
        {
            return;
        }

        stored.RevokedAtUtc = clock.GetUtcNow().UtcDateTime;
        stored.RevokedReason = "logout";
        await db.SaveChangesAsync(ct);
    }

    private async Task RevokeChainAsync(int userId, DateTime now, string reason, CancellationToken ct)
    {
        await db.RefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAtUtc == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.RevokedAtUtc, now)
                      .SetProperty(t => t.RevokedReason, reason),
                ct);
    }

    private async Task<string> CreateAccessTokenAsync(ApplicationUser user, DateTime now)
    {
        var roles = await users.GetRolesAsync(user);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(ClaimTypes.Name, user.UserName ?? string.Empty),
            new("full_name", user.FullName),
            new("tenant_id", user.TenantId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };

        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now,
            expires: now.AddMinutes(_options.AccessTokenMinutes),
            signingCredentials: new SigningCredentials(keys.Key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<string> CreateRefreshTokenAsync(ApplicationUser user, DateTime now, CancellationToken ct)
    {
        // 256 bits of entropy. Returned to the caller once; only the hash is
        // stored, so a database dump yields nothing usable.
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TenantId = user.TenantId,
            TokenHash = Hash(raw),
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(_options.RefreshTokenDays),
        });

        await db.SaveChangesAsync(ct);
        return raw;
    }

    private static string Hash(string raw)
        => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)));
}
