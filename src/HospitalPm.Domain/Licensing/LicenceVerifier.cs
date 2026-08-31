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
    /// Decides the verdict for a licence file's contents.
    /// </summary>
    /// <param name="fileText">The file, or null when there is no licence installed.</param>
    /// <param name="today">The hospital's today, for the expiry comparison.</param>
    public LicenceStatus Verify(string? fileText, DateOnly today)
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

        if (licence.ExpiresOn is { } expiry && today > expiry)
        {
            var days = today.DayNumber - expiry.DayNumber;

            // Expired, and still returning the licence: the hospital name and
            // support id stay useful, and nothing here stops the app.
            return new LicenceStatus(LicenceState.Expired, licence,
                $"The licence for {licence.HospitalName} expired on "
                + $"{expiry:dd/MM/yyyy}, {days} day{(days == 1 ? "" : "s")} ago. "
                + "The software keeps working — contact your supplier to renew.");
        }

        var until = licence.ExpiresOn is { } e
            ? $"valid until {e:dd/MM/yyyy}"
            : "perpetual";

        return new LicenceStatus(LicenceState.Valid, licence,
            $"Licensed to {licence.HospitalName}, {until}.");
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
