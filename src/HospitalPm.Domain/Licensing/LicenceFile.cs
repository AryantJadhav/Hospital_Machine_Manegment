using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HospitalPm.Domain.Licensing;

/// <summary>
/// Reads and writes the licence file.
///
/// The payload is stored as an opaque base64 blob and signed as those exact
/// bytes. That removes the usual trap in signed JSON: if you re-serialise
/// before verifying, any difference in property order, spacing or number
/// formatting breaks a signature that was perfectly good. Here the bytes that
/// were signed are the bytes that are checked, and the JSON is only parsed
/// afterwards.
///
/// The wrapper is plain text so a licence survives being pasted into an
/// email, which is how it will actually reach a hospital.
/// </summary>
public static class LicenceFile
{
    /// <summary>The kind of signed file this wrapper carries by default: a licence.</summary>
    public const string LicenceKind = "LICENCE";

    private const string SignatureMarker = "-----SIGNATURE-----";

    private static string Begin(string kind) => $"-----BEGIN HOSPITALPM {kind}-----";

    private static string End(string kind) => $"-----END HOSPITALPM {kind}-----";

    /// <summary>Line length for the base64 blocks. Long enough to be compact, short enough to survive email.</summary>
    private const int WrapAt = 76;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The payload bytes and the signature over them, exactly as stored.</summary>
    public sealed record Parsed(byte[] Payload, byte[] Signature);

    /// <param name="kind">
    /// What the file is. A different kind has different markers, so a file of one kind is never read as another:
    /// a lock code pasted where a licence belongs is "not a licence file", whatever it was signed with.
    /// </param>
    public static string Format(byte[] payload, byte[] signature, string kind = LicenceKind)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(signature);

        var builder = new StringBuilder();
        builder.AppendLine(Begin(kind));
        AppendWrapped(builder, Convert.ToBase64String(payload));
        builder.AppendLine(SignatureMarker);
        AppendWrapped(builder, Convert.ToBase64String(signature));
        builder.AppendLine(End(kind));
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
    /// Splits a licence file into payload and signature. Returns null for
    /// anything that is not a licence file; callers treat that as Invalid
    /// rather than crashing, because this text arrives by email and gets
    /// mangled.
    /// </summary>
    public static Parsed? Parse(string text, string kind = LicenceKind)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lines = text.Split('\n')
            .Select(l => l.Trim().Trim('\r'))
            .Where(l => l.Length > 0)
            .ToList();

        var begin = lines.IndexOf(Begin(kind));
        var middle = lines.IndexOf(SignatureMarker);
        var end = lines.IndexOf(End(kind));

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

    public static byte[] Serialise(Licence licence) =>
        JsonSerializer.SerializeToUtf8Bytes(licence, Json);

    /// <summary>Reads the payload back. Only ever called on bytes whose signature already verified.</summary>
    public static Licence? Deserialise(byte[] payload)
    {
        try
        {
            return JsonSerializer.Deserialize<Licence>(payload, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
