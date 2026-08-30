namespace HospitalPm.Infrastructure.Identity;

/// <summary>
/// A refresh token, stored only as a SHA-256 hash.
///
/// The raw token is returned to the caller once and never persisted: a
/// database dump or a leaked backup must not yield usable credentials.
/// Tokens rotate on every use, and using an already-rotated token revokes
/// the whole chain — the standard detection for a stolen token being
/// replayed alongside the legitimate one.
/// </summary>
public sealed class RefreshToken
{
    public long Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int UserId { get; set; }

    /// <summary>SHA-256 of the raw token. Never the token itself.</summary>
    public required string TokenHash { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>Set when this token was rotated, pointing at its replacement.</summary>
    public long? ReplacedByTokenId { get; set; }

    /// <summary>Free-text reason, e.g. "rotated", "logout", "reuse detected".</summary>
    public string? RevokedReason { get; set; }

    public ApplicationUser? User { get; set; }

    public bool IsActive(DateTime utcNow) => RevokedAtUtc is null && ExpiresAtUtc > utcNow;
}
