using Microsoft.Extensions.Options;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace HospitalPm.Infrastructure.Labels;

/// <summary>
/// Lays asset tags out on A4 sheets of adhesive labels.
///
/// A4 rather than Letter: this ships to Indian hospitals, where Letter paper
/// is not sold. The grid is 3 x 8 at 63.5 x 33.9 mm, which matches the
/// commonest off-the-shelf 24-per-sheet stock, so a hospital can print onto
/// label paper bought from any stationer rather than needing a dedicated
/// printer on day one.
/// </summary>
public sealed class LabelSheetService(QrCodeService qr, IOptions<LabelOptions> options)
{
    // Lato, not Calibri or Arial. QuestPDF ships Lato inside the package, so it
    // renders identically on a Windows PC and a minimal Linux container with no
    // system fonts installed at all. Naming a host font means the document looks
    // different on the hospital's server than it did in testing, and on a
    // stripped container there may be no font to fall back to.
    private const string BundledFont = "Lato";

    private readonly LabelOptions _options = options.Value;

    private const int Columns = 3;
    private const int Rows = 8;
    private const float LabelWidthMm = 63.5f;
    private const float LabelHeightMm = 33.9f;

    public byte[] Render(IReadOnlyList<LabelData> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        // Pre-rendered so the QR for each asset is generated once, not once
        // per layout pass.
        var codes = labels.ToDictionary(
            l => l.AssetTag,
            l => qr.RenderPng(_options.BaseUrl, l.AssetTag, pixelsPerModule: 8),
            StringComparer.Ordinal);

        var pages = labels.Chunk(Columns * Rows).ToList();

        return Document.Create(doc =>
        {
            foreach (var page in pages)
            {
                doc.Page(p =>
                {
                    p.Size(PageSizes.A4);
                    p.Margin(6, Unit.Millimetre);
                    p.DefaultTextStyle(t => t.FontFamily(BundledFont).FontSize(8));

                    p.Content().Column(sheet =>
                    {
                        // Built from Column and Row rather than Grid, which
                        // QuestPDF deprecated in 2022.11.
                        foreach (var line in page.Chunk(Columns))
                        {
                            sheet.Item().Row(row =>
                            {
                                foreach (var label in line)
                                {
                                    row.ConstantItem(LabelWidthMm, Unit.Millimetre)
                                       .Height(LabelHeightMm, Unit.Millimetre)
                                       .Padding(2, Unit.Millimetre)
                                       .Row(cell =>
                                       {
                                           cell.ConstantItem(24, Unit.Millimetre)
                                               .AlignMiddle()
                                               .Image(codes[label.AssetTag]);

                                           cell.RelativeItem().PaddingLeft(2, Unit.Millimetre).Column(col =>
                                           {
                                               // Printed large and human
                                               // readable: when a label is
                                               // scratched past scanning,
                                               // which is when it matters
                                               // most, this is what a
                                               // technician types by hand.
                                               col.Item().Text(label.AssetTag)
                                                  .FontSize(10).Bold();

                                               col.Item().Text(label.EquipmentTypeName)
                                                  .FontSize(7).LineHeight(1.1f);

                                               col.Item().Text(label.LocationName)
                                                  .FontSize(7).FontColor(Colors.Grey.Darken1);

                                               if (!string.IsNullOrWhiteSpace(_options.HospitalName))
                                               {
                                                   col.Item().PaddingTop(1)
                                                      .Text(_options.HospitalName)
                                                      .FontSize(6).FontColor(Colors.Grey.Medium);
                                               }
                                           });
                                       });
                                }
                            });
                        }
                    });
                });
            }
        }).GeneratePdf();
    }
}
