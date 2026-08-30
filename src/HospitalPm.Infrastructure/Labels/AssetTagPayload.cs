namespace HospitalPm.Infrastructure.Labels;

/// <summary>
/// What actually gets encoded into an asset-tag QR code.
///
/// A URL rather than a bare tag, so a technician who scans with the phone's
/// own camera lands on the equipment page instead of seeing meaningless
/// text. The tag sits in the path, so our mobile app can still read it even
/// if the hostname later changes — a label glued to a ventilator has to keep
/// working for a decade, long after somebody renames the server.
///
/// The base URL is configurable because Phase 2 publishes the service as
/// hospitalpm.local over mDNS, and a hospital that puts it on a fixed IP
/// needs the printed labels to match.
/// </summary>
public sealed class LabelOptions
{
    public const string SectionName = "Labels";

    /// <summary>
    /// Origin printed into every QR code. Defaults to the mDNS name the
    /// Phase 2 installer registers.
    /// </summary>
    public string BaseUrl { get; set; } = "http://hospitalpm.local";

    /// <summary>Shown on the label so a found asset can be traced to its owner.</summary>
    public string HospitalName { get; set; } = string.Empty;
}

public static class AssetTagPayload
{
    /// <summary>
    /// Builds the scan target for an asset tag.
    /// </summary>
    public static string Build(string baseUrl, string assetTag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetTag);

        var root = (baseUrl ?? string.Empty).TrimEnd('/');

        // Escaped because asset tags are the hospital's own strings and
        // routinely contain slashes and spaces ("ICU/VENT 03").
        return $"{root}/e/{Uri.EscapeDataString(assetTag.Trim())}";
    }

    /// <summary>
    /// Recovers the tag from a scanned payload.
    ///
    /// Accepts a bare tag too: labels printed before this scheme existed, and
    /// hand-typed entry when a label is too scratched to scan, both arrive
    /// without the URL wrapper.
    /// </summary>
    public static string Extract(string scanned)
    {
        if (string.IsNullOrWhiteSpace(scanned))
        {
            return string.Empty;
        }

        var text = scanned.Trim();

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return text;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Length >= 2 && segments[^2].Equals("e", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(segments[^1])
            : text;
    }
}
