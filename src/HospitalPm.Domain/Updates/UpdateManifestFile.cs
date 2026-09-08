using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HospitalPm.Domain.Updates;

/// <summary>
/// Reads and writes the .update file.
///
/// Same envelope as a licence, and for the same reason: the payload is an
/// opaque base64 blob signed as those exact bytes, so the bytes that were
/// signed are the bytes that get checked. Re-serialising JSON before
/// verifying is the classic way to break a signature that was perfectly good.
///
/// Kept as a separate type from LicenceFile rather than shared. They are the
/// same shape today, but one gates a commercial entitlement and the other
/// gates code that will run as SYSTEM; a change made for one must never
/// silently alter the other.
/// </summary>
public static class UpdateManifestFile
{
    private const string Begin = "-----BEGIN HOSPITALPM UPDATE-----";
    private const string SignatureMarker = "-----SIGNATURE-----";
    private const string End = "-----END HOSPITALPM UPDATE-----";

    private const int WrapAt = 76;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The payload bytes and the signature over them, exactly as stored.</summary>
    public sealed record Parsed(byte[] Payload, byte[] Signature);

    public static string Format(byte[] payload, byte[] signature)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(signature);

        var builder = new StringBuilder();
        builder.AppendLine(Begin);
        AppendWrapped(builder, Convert.ToBase64String(payload));
        builder.AppendLine(SignatureMarker);
        AppendWrapped(builder, Convert.ToBase64String(signature));
        builder.AppendLine(End);
        return builder.ToString();
    }

    private static void AppendWrapped(StringBuilder builder, string base64)
    {
        for (var i = 0; i < base64.Length; i += WrapAt)
        {
            builder.AppendLine(base64.Substring(i, Math.Min(WrapAt, base64.Length - i)));
        }
    }

    /// <summary>
    /// Splits an update file into payload and signature. Returns null for
    /// anything that is not one, which callers report rather than throw on:
    /// this file arrives on a USB stick and may be truncated, renamed or
    /// simply the wrong file.
    /// </summary>
    public static Parsed? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lines = text.Split('\n')
            .Select(l => l.Trim().Trim('\r'))
            .Where(l => l.Length > 0)
            .ToList();

        var begin = lines.IndexOf(Begin);
        var middle = lines.IndexOf(SignatureMarker);
        var end = lines.IndexOf(End);

        if (begin < 0 || middle <= begin || end <= middle) return null;

        var payload = string.Concat(lines.Skip(begin + 1).Take(middle - begin - 1));
        var signature = string.Concat(lines.Skip(middle + 1).Take(end - middle - 1));

        if (payload.Length == 0 || signature.Length == 0) return null;

        try
        {
            return new Parsed(Convert.FromBase64String(payload), Convert.FromBase64String(signature));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static byte[] Serialise(UpdateManifest manifest) =>
        JsonSerializer.SerializeToUtf8Bytes(manifest, Json);

    /// <summary>Reads the payload back. Only ever called on bytes whose signature already verified.</summary>
    public static UpdateManifest? Deserialise(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdateManifest>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
