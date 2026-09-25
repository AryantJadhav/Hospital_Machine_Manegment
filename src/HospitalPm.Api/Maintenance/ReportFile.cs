using System.Text;

namespace HospitalPm.Api.Maintenance;

/// <summary>
/// Checking a report file before it is stored.
///
/// What kind of file it is comes from its first bytes, never from its name or from what the
/// browser said. A file called report.pdf that is really a web page would, served back from
/// the application's own address, run in the application's own page. So only four kinds are
/// accepted (a PDF, or a JPEG, PNG or WebP photo), each recognised by its signature, and
/// they are sent back with the type found here and told not to be second-guessed.
/// </summary>
public static class ReportFile
{
    /// <summary>A phone photo or a scanned report is a few megabytes; more than this is a mistake.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>Most reports are one or two files. Ten is a generous ceiling, not a target.</summary>
    public const int MaxFilesPerPm = 10;

    public const string Pdf = "application/pdf";
    public const string Jpeg = "image/jpeg";
    public const string Png = "image/png";
    public const string Webp = "image/webp";

    /// <summary>The kind of file these bytes are, or null when they are none of the four.</summary>
    public static string? Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 5 && bytes[..5].SequenceEqual("%PDF-"u8))
        {
            return Pdf;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return Jpeg;
        }

        if (bytes.Length >= 8 && bytes[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return Png;
        }

        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
        {
            return Webp;
        }

        return null;
    }

    private static string Extension(string contentType) => contentType switch
    {
        Pdf => ".pdf",
        Jpeg => ".jpg",
        Png => ".png",
        Webp => ".webp",
        _ => string.Empty,
    };

    /// <summary>
    /// The name to keep. Any folder the browser sent is dropped, control and path characters
    /// are removed, and the extension is made to match what the file really is, so a report
    /// saved as "scan.txt" that is a PDF is stored, and later downloaded, as a PDF.
    /// </summary>
    public static string CleanName(string? declared, string contentType)
    {
        var name = declared ?? string.Empty;
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '"', ';', '%' };
        var sb = new StringBuilder();
        foreach (var ch in name)
        {
            if (!char.IsControl(ch) && !invalid.Contains(ch))
            {
                sb.Append(ch);
            }
        }

        var stem = Path.GetFileNameWithoutExtension(sb.ToString()).Trim();
        if (stem.Length == 0)
        {
            stem = "report";
        }

        var extension = Extension(contentType);
        var full = stem + extension;

        // Keeps the name within the column, cutting the stem and never the extension.
        const int max = 200;
        return full.Length <= max ? full : stem[..(max - extension.Length)] + extension;
    }
}
