namespace HospitalPm.Infrastructure.Identity;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// Issuer and audience are both local. There is no external identity
    /// provider — the install must authenticate with the network cable out.
    /// </summary>
    public string Issuer { get; set; } = "hospitalpm";

    public string Audience { get; set; } = "hospitalpm";

    /// <summary>
    /// Short by design. A revoked or deactivated user keeps working until
    /// their access token expires, because validating a JWT deliberately
    /// does not hit the database. Fifteen minutes bounds that window; the
    /// refresh token is where revocation actually bites.
    /// </summary>
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>
    /// Long enough that a technician on a ward round is not re-authenticating
    /// all day, short enough that a lost tablet stops working within a shift
    /// cycle.
    /// </summary>
    public int RefreshTokenDays { get; set; } = 14;
}
