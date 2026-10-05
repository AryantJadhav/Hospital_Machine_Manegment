using System.Security.Cryptography;

namespace HospitalPm.Domain.Licensing;

/// <summary>What a signed code tells an installation to do.</summary>
public static class LicenceAction
{
    /// <summary>Nobody can sign in and no record opens, until an unlock code is entered.</summary>
    public const string Lock = "lock";

    public const string Unlock = "unlock";

    public static bool IsKnown(string? action) => action is Lock or Unlock;
}

/// <summary>
/// A short signed instruction to one installation: lock it, or unlock it.
///
/// Made on our side with the signing key, sent by message, and entered at the hospital. The installation
/// checks it offline against the public key built into the program, exactly as it checks a licence, so there is
/// no network call and nothing to host.
/// </summary>
/// <param name="LicenceId">The licence of the installation it is for. A code for another licence does nothing.</param>
/// <param name="Action"><see cref="LicenceAction"/>.</param>
/// <param name="Sequence">
/// A number that only goes up for each licence. A code is accepted only if its number is higher than the last one
/// the installation has accepted, so an old unlock cannot undo a newer lock, an old lock cannot undo a newer
/// unlock, and a code used twice does nothing the second time.
/// </param>
/// <param name="IssuedAtUtc">For the person reading it back to a hospital on the phone. Never used to decide anything.</param>
public sealed record LicenceCommand(Guid LicenceId, string Action, long Sequence, DateTime IssuedAtUtc);

/// <summary>
/// Signs and checks a <see cref="LicenceCommand"/>.
///
/// The signature is over a fixed prefix and the payload, never the payload alone. A licence is signed over its
/// payload alone, so without the prefix a signature made for a licence could in principle be offered as a
/// command (or the other way round). With it, neither checks as the other, and the different file markers
/// (see <see cref="LicenceFile"/>) stop either being pasted in the wrong place.
/// </summary>
public static class LicenceCommandFile
{
    /// <summary>The kind of file in <see cref="LicenceFile"/>'s wrapper: "HOSPITALPM CODE".</summary>
    public const string Kind = "CODE";

    private static readonly byte[] Prefix = "HPCMD1\0"u8.ToArray();

    private static byte[] Signed(byte[] payload) => [.. Prefix, .. payload];

    public static string Sign(LicenceCommand command, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(privateKey);

        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(command, LicenceFile.Json);
        var signature = privateKey.SignData(Signed(payload), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return LicenceFile.Format(payload, signature, Kind);
    }

    /// <summary>
    /// The command in a pasted code, if the code is whole and signed by us; null for anything else, whatever the
    /// reason. The reason is deliberately not said: it would only help someone experimenting.
    /// </summary>
    public static LicenceCommand? Verify(string? text, string publicKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(publicKeyBase64))
        {
            return null;
        }

        var parsed = LicenceFile.Parse(text, Kind);
        if (parsed is null)
        {
            return null;
        }

        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);

            if (!ecdsa.VerifyData(Signed(parsed.Payload), parsed.Signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                return null;
            }

            var command = System.Text.Json.JsonSerializer.Deserialize<LicenceCommand>(parsed.Payload, LicenceFile.Json);
            return command is not null
                   && command.LicenceId != Guid.Empty
                   && LicenceAction.IsKnown(command.Action)
                   && command.Sequence > 0
                ? command
                : null;
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
