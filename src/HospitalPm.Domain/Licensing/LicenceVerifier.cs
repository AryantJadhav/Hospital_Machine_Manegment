using System.Security.Cryptography;

namespace HospitalPm.Domain.Licensing;

/// <summary>
/// Checks a licence against our public key, entirely offline.
///
/// ECDSA over P-256 with SHA-256. Chosen because it is in the base class
/// library — no new dependency, nothing to install on the hospital's machine,
/// and no network call on a critical path. The signature is small enough that
/// the whole licence stays email-sized.
///
/// This class can only verify. The private key and the signing code live in a
/// vendor-side tool that is never shipped, so the binary a hospital runs is
/// incapable of minting a licence for itself.
/// </summary>
public sealed class LicenceVerifier(string publicKeyBase64)
{
    /// <summary>
    /// Days after the end date during which everything keeps working. Two weeks
    /// covers a purchase order that is a few days late.
    /// </summary>
    public const int DefaultGraceDays = 14;

    /// <summary>
    /// The licence in a file, if its signature is good; null for anything else.
    /// Used to find out which licence a file is before deciding when it began.
    /// </summary>
    public Licence? Trusted(string? fileText)
    {
        if (string.IsNullOrWhiteSpace(fileText)) return null;

        var parsed = LicenceFile.Parse(fileText);
        return parsed is not null && SignatureIsGood(parsed) ? LicenceFile.Deserialise(parsed.Payload) : null;
    }

    /// <summary>
    /// Decides the verdict for a licence file's contents.
    /// </summary>
    /// <param name="fileText">The file, or null when there is no licence installed.</param>
    /// <param name="today">The hospital's today, for the expiry comparison.</param>
    /// <param name="activatedOn">
    /// The day a duration-based licence began. Left out, it begins today, which
    /// is what a licence being installed for the first time should do.
    /// </param>
    /// <param name="graceDays">Days after the end date before the software turns read-only.</param>
    public LicenceStatus Verify(
        string? fileText,
        DateOnly today,
        DateOnly? activatedOn = null,
        int graceDays = DefaultGraceDays)
    {
        if (string.IsNullOrWhiteSpace(fileText))
        {
            return new LicenceStatus(LicenceState.Missing, null,
                "No licence is installed. The software is running unlicensed.");
        }

        var parsed = LicenceFile.Parse(fileText);
        if (parsed is null)
        {
            return new LicenceStatus(LicenceState.Invalid, null,
                "The licence file could not be read. It may have been damaged in transit — "
                + "ask for it to be sent again.");
        }

        if (!SignatureIsGood(parsed))
        {
            // Deliberately does not distinguish "edited" from "signed by
            // someone else". Both mean the same thing to the person reading
            // it, and saying more only helps whoever is experimenting.
            return new LicenceStatus(LicenceState.Invalid, null,
                "The licence file is not valid for this software. "
                + "If it was edited after being issued, use the original.");
        }

        var licence = LicenceFile.Deserialise(parsed.Payload);
        if (licence is null)
        {
            return new LicenceStatus(LicenceState.Invalid, null,
                "The licence file is signed but its contents could not be read. "
                + "It may have been issued for a newer version.");
        }

        var expiry = EffectiveExpiry(licence, activatedOn ?? today);

        if (expiry is null)
        {
            return new LicenceStatus(LicenceState.Valid, licence,
                $"Licensed to {licence.HospitalName}, perpetual.");
        }

        var end = expiry.Value;
        var readOnlyFrom = end.AddDays(graceDays + 1);

        if (today >= readOnlyFrom)
        {
            // Still returning the licence: the hospital name and support id stay
            // useful, and reading the records must keep working.
            return new LicenceStatus(LicenceState.ReadOnly, licence,
                $"The licence for {licence.HospitalName} expired on {end:dd/MM/yyyy} and the "
                + $"{graceDays}-day grace period has ended. The software is now read-only: records can be "
                + "viewed and printed, but nothing new can be recorded. Install a renewal key on the "
                + "Licence page to carry on.",
                end, readOnlyFrom);
        }

        if (today > end)
        {
            var days = today.DayNumber - end.DayNumber;

            return new LicenceStatus(LicenceState.Expired, licence,
                $"The licence for {licence.HospitalName} expired on "
                + $"{end:dd/MM/yyyy}, {days} day{(days == 1 ? "" : "s")} ago. "
                + $"Everything still works until {readOnlyFrom.AddDays(-1):dd/MM/yyyy}; after that the "
                + "software is read-only. Contact your supplier for a renewal key.",
                end, readOnlyFrom);
        }

        var left = end.DayNumber - today.DayNumber;
        var soon = left <= 30
            ? $", {left} day{(left == 1 ? "" : "s")} left"
            : string.Empty;

        return new LicenceStatus(LicenceState.Valid, licence,
            $"Licensed to {licence.HospitalName}, valid until {end:dd/MM/yyyy}{soon}.",
            end, readOnlyFrom);
    }

    /// <summary>
    /// The last day a licence is current, or null if it never ends. A licence
    /// with a fixed date and a duration ends at whichever comes first.
    /// </summary>
    public static DateOnly? EffectiveExpiry(Licence licence, DateOnly startedOn)
    {
        DateOnly? byDuration = licence.DurationDays is { } d and > 0 ? startedOn.AddDays(d) : null;

        return (licence.ExpiresOn, byDuration) switch
        {
            ({ } a, { } b) => a < b ? a : b,
            ({ } a, null) => a,
            (null, { } b) => b,
            _ => null,
        };
    }

    private bool SignatureIsGood(LicenceFile.Parsed parsed)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);

            return ecdsa.VerifyData(
                parsed.Payload, parsed.Signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            // A malformed signature, or a public key this build cannot parse.
            // Either way the licence is not trustworthy.
            return false;
        }
    }
}
