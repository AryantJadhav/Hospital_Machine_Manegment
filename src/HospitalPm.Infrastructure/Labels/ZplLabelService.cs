using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace HospitalPm.Infrastructure.Labels;

/// <summary>
/// Emits ZPL II for Zebra-compatible label printers.
///
/// Printing a PDF to a thermal label printer works badly: the driver
/// rasterises it, alignment drifts, and a hospital ends up with QR codes
/// half off the edge of a 50 mm label. ZPL is what these printers speak
/// natively, so the printer renders the barcode itself at full resolution.
///
/// The output is plain text sent straight to the printer's raw port (9100)
/// or dropped on a share, which is how biomedical departments already drive
/// them. That also keeps this off the critical path for an install with no
/// label printer at all — those hospitals use the A4 sheet instead.
/// </summary>
public sealed class ZplLabelService(IOptions<LabelOptions> options)
{
    private readonly LabelOptions _options = options.Value;

    /// <summary>
    /// Dots per millimetre for a 203 dpi printer, which is the common desktop
    /// Zebra. A 300 dpi unit needs 11.8; exposed so a hospital with one is not
    /// stuck reprinting everything at two-thirds size.
    /// </summary>
    public const double DotsPerMm203 = 8.0;

    public string Render(IReadOnlyList<LabelData> labels, double widthMm = 50, double heightMm = 25, double dotsPerMm = DotsPerMm203)
    {
        ArgumentNullException.ThrowIfNull(labels);

        var sb = new StringBuilder();

        foreach (var label in labels)
        {
            var widthDots = (int)Math.Round(widthMm * dotsPerMm, MidpointRounding.AwayFromZero);
            var payload = AssetTagPayload.Build(_options.BaseUrl, label.AssetTag);

            sb.Append("^XA");
            sb.Append(CultureInfo.InvariantCulture, $"^PW{widthDots}");
            sb.Append(CultureInfo.InvariantCulture, $"^LL{(int)Math.Round(heightMm * dotsPerMm, MidpointRounding.AwayFromZero)}");
            sb.Append("^LH0,0");

            // ^BQ renders the QR on the printer itself. Magnification 4 keeps
            // a URL-length payload scannable on a 25 mm label; the 'QA,' prefix
            // selects automatic mode with error correction M.
            sb.Append("^FO16,16^BQN,2,4^FDQA,");
            sb.Append(Escape(payload));
            sb.Append("^FS");

            // Human-readable tag, large. This is what gets used when the code
            // is too scratched to scan.
            sb.Append(CultureInfo.InvariantCulture, $"^FO{(int)(24 * dotsPerMm)},20^A0N,28,28^FD");
            sb.Append(Escape(label.AssetTag));
            sb.Append("^FS");

            sb.Append(CultureInfo.InvariantCulture, $"^FO{(int)(24 * dotsPerMm)},56^A0N,18,18^FD");
            sb.Append(Escape(Truncate(label.EquipmentTypeName, 24)));
            sb.Append("^FS");

            sb.Append(CultureInfo.InvariantCulture, $"^FO{(int)(24 * dotsPerMm)},78^A0N,16,16^FD");
            sb.Append(Escape(Truncate(label.LocationName, 26)));
            sb.Append("^FS");

            if (!string.IsNullOrWhiteSpace(_options.HospitalName))
            {
                sb.Append(CultureInfo.InvariantCulture, $"^FO{(int)(24 * dotsPerMm)},98^A0N,14,14^FD");
                sb.Append(Escape(Truncate(_options.HospitalName, 30)));
                sb.Append("^FS");
            }

            sb.Append("^XZ\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// ZPL treats ^ and ~ as command introducers, so a tag containing either
    /// would truncate the field and print garbage. Hospitals do use ^ in asset
    /// codes.
    /// </summary>
    private static string Escape(string value)
        => value.Replace("^", " ", StringComparison.Ordinal)
                .Replace("~", " ", StringComparison.Ordinal);

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..(max - 1)] + "…";
}
