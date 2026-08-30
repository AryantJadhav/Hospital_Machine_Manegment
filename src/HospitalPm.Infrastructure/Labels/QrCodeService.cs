using QRCoder;

namespace HospitalPm.Infrastructure.Labels;

/// <summary>
/// Renders asset-tag QR codes.
///
/// Uses PngByteQRCode rather than the System.Drawing-based renderer: that
/// one needs GDI+ on Windows and libgdiplus on Linux, neither of which an
/// air-gapped hospital server can be assumed to have. This path is fully
/// managed and produces the same bytes on both platforms.
/// </summary>
public sealed class QrCodeService
{
    /// <summary>
    /// Error correction level M tolerates roughly 15% damage.
    ///
    /// Chosen over L because these labels live on equipment that gets wiped
    /// down with disinfectant several times a day for years. H would survive
    /// more but makes the pattern denser, and a denser code is harder for a
    /// cheap phone camera to read at arm's length in poor ward lighting.
    /// </summary>
    private const QRCodeGenerator.ECCLevel Ecc = QRCodeGenerator.ECCLevel.M;

    // Deliberately an instance method on an injected service rather than a
    // static helper: the ECC level and module size become configurable per
    // install once hospitals start reporting scan failures on their own
    // label stock, and callers should not have to change then.
#pragma warning disable CA1822
    public byte[] RenderPng(string baseUrl, string assetTag, int pixelsPerModule = 10)
    {
        var payload = AssetTagPayload.Build(baseUrl, assetTag);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, Ecc);
        var png = new PngByteQRCode(data);

        return png.GetGraphic(pixelsPerModule);
    }
#pragma warning restore CA1822
}
