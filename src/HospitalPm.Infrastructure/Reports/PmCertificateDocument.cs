using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace HospitalPm.Infrastructure.Reports;

/// <summary>
/// The document a hospital hands an auditor.
///
/// Two things make it credible rather than decorative: it prints the exact
/// checklist version the technician filled under, and it does not pretend a
/// PM with out-of-spec readings was a clean pass. A certificate that hides
/// findings is worse than no certificate, because it makes the whole register
/// untrustworthy the first time someone checks one.
/// </summary>
public sealed class PmCertificateDocument(
    PmCertificateData data,
    ReportOptions options,
    TimeSpan utcOffset = default) : IDocument
{
    // Lato, not Calibri or Arial. QuestPDF ships Lato inside the package, so it
    // renders identically on a Windows PC and a minimal Linux container with no
    // system fonts installed at all. Naming a host font means the document looks
    // different on the hospital's server than it did in testing, and on a
    // stripped container there may be no font to fall back to.
    private const string BundledFont = "Lato";

    private static readonly string Accent = Colors.Blue.Darken2;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            // A4: this ships to Indian hospitals, where Letter is not sold.
            page.Size(PageSizes.A4);
            page.Margin(18, Unit.Millimetre);
            page.DefaultTextStyle(t => t.FontFamily(BundledFont).FontSize(9.5f));

            page.Header().Element(Header);
            page.Content().Element(Content);
            page.Footer().Element(Footer);
        });
    }

    private void Header(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().Text(string.IsNullOrWhiteSpace(options.HospitalName)
                            ? "Hospital PM"
                            : options.HospitalName)
                        .FontSize(15).Bold();

                    if (!string.IsNullOrWhiteSpace(options.HospitalSubtitle))
                    {
                        left.Item().Text(options.HospitalSubtitle!)
                            .FontSize(9).FontColor(Colors.Grey.Darken1);
                    }

                    left.Item().PaddingTop(2).Text("Biomedical Engineering Department")
                        .FontSize(9).FontColor(Colors.Grey.Darken1);
                });

                row.ConstantItem(58, Unit.Millimetre).AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text("PREVENTIVE MAINTENANCE")
                        .FontSize(10).Bold().FontColor(Accent);
                    right.Item().AlignRight().Text("CERTIFICATE").FontSize(10).Bold().FontColor(Accent);

                    // The verdict, stated at the top rather than buried in a
                    // table an auditor has to read line by line.
                    right.Item().PaddingTop(4).AlignRight()
                        .Background(data.IsClean ? Colors.Green.Lighten4
                            : data.FailedCount > 0 ? Colors.Red.Lighten4 : Colors.Orange.Lighten4)
                        .Padding(4)
                        .Text(data.Verdict)
                        .FontSize(8.5f)
                        .FontColor(data.IsClean ? Colors.Green.Darken3
                            : data.FailedCount > 0 ? Colors.Red.Darken3 : Colors.Orange.Darken4);
                });
            });

            col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Accent);
        });
    }

    private void Content(IContainer container)
    {
        container.PaddingVertical(10).Column(col =>
        {
            col.Spacing(12);

            col.Item().Element(EquipmentBlock);
            col.Item().Element(ChecklistTable);
            col.Item().Element(SignOffBlock);
        });
    }

    private void EquipmentBlock(IContainer container)
    {
        container.Border(1).BorderColor(Colors.Grey.Lighten1).Padding(8).Row(row =>
        {
            row.RelativeItem().Column(left =>
            {
                Field(left, "Asset tag", data.AssetTag, bold: true);
                Field(left, "Equipment", data.EquipmentTypeName);
                Field(left, "Location", data.LocationName);
                Field(left, "Serial number", data.SerialNumber);
            });

            row.RelativeItem().Column(right =>
            {
                Field(right, "Manufacturer", data.Manufacturer);
                Field(right, "Model", data.Model);
                Field(right, "Checklist", $"{data.ChecklistName} (v{data.ChecklistVersionNo})");
                Field(right, "Scheduled for", data.DueDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
            });
        });
    }

    private void ChecklistTable(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().PaddingBottom(4).Text("Checks performed").FontSize(11).Bold();

            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(4);   // check
                    c.RelativeColumn(2);   // expected
                    c.RelativeColumn(2);   // result
                });

                table.Header(header =>
                {
                    HeaderCell(header.Cell(), "Check");
                    HeaderCell(header.Cell(), "Expected");
                    HeaderCell(header.Cell(), "Result");
                });

                string? lastSection = null;

                foreach (var line in data.Lines)
                {
                    if (line.SectionTitle != lastSection)
                    {
                        lastSection = line.SectionTitle;
                        table.Cell().ColumnSpan(3)
                            .Background(Colors.Grey.Lighten3).PaddingVertical(3).PaddingHorizontal(4)
                            .Text(line.SectionTitle).FontSize(9).Bold();
                    }

                    var background = line.Failed ? Colors.Red.Lighten5
                        : line.OutOfRange ? Colors.Orange.Lighten5 : Colors.White;

                    table.Cell().Background(background).Element(Cell).Column(c =>
                    {
                        c.Item().Text(line.Label);
                        if (!string.IsNullOrWhiteSpace(line.Note))
                        {
                            c.Item().Text(line.Note!).FontSize(8).Italic()
                                .FontColor(Colors.Grey.Darken2);
                        }
                    });

                    table.Cell().Background(background).Element(Cell)
                        .Text(line.Expected ?? "—").FontColor(Colors.Grey.Darken1);

                    table.Cell().Background(background).Element(Cell).Text(text =>
                    {
                        text.Span(line.Answer).Bold();
                        // Marked in the row itself, not only summarised at
                        // the top, so a finding survives being photocopied.
                        if (line.Failed)
                        {
                            text.Span("  FAILED").FontSize(8)
                                .FontColor(Colors.Red.Darken3).Bold();
                        }
                        else if (line.OutOfRange)
                        {
                            text.Span("  OUT OF SPEC").FontSize(8)
                                .FontColor(Colors.Orange.Darken3).Bold();
                        }
                    });
                }
            });
        });
    }

    private void SignOffBlock(IContainer container)
    {
        container.Row(row =>
        {
            row.RelativeItem().Column(left =>
            {
                Field(left, "Performed by", data.SignedByName ?? data.TechnicianName, bold: true);

                // The device time, which is when the work actually happened.
                // The received time can be hours later if the phone was
                // offline on a ward, and the auditor wants the former.
                var zone = ReportTime.Zone(utcOffset);

                Field(left, "Performed on",
                    $"{ReportTime.DateTime(data.PerformedAtUtc ?? data.CompletedAtUtc, utcOffset)} {zone}");

                // Also on the hospital's clock. It used to print UTC, which put a
                // second, different time beside the first for no reader's benefit.
                Field(left, "Recorded at",
                    $"{ReportTime.DateTime(data.CompletedAtUtc, utcOffset)} {zone}");

                if (!string.IsNullOrWhiteSpace(data.Notes))
                {
                    left.Item().PaddingTop(6).Text("Notes").FontSize(8)
                        .FontColor(Colors.Grey.Darken1);
                    left.Item().Text(data.Notes!);
                }
            });

            row.ConstantItem(70, Unit.Millimetre).Column(right =>
            {
                right.Item().Text("Signature").FontSize(8).FontColor(Colors.Grey.Darken1);

                right.Item().Height(28, Unit.Millimetre)
                    .Border(1).BorderColor(Colors.Grey.Lighten1)
                    .Padding(3)
                    .Element(SignatureImage);
            });
        });
    }

    private void SignatureImage(IContainer container)
    {
        if (data.Signature is null || data.Signature.Length == 0 || !IsRenderableSignature())
        {
            container.AlignMiddle().AlignCenter()
                .Text("Not signed").FontSize(8).FontColor(Colors.Grey.Medium);
            return;
        }

        // Note: QuestPDF renders lazily, so a try/catch here would not catch
        // a layout failure — that happens later inside GeneratePdf(). The
        // signature is therefore validated before it reaches the document,
        // and the container is bounded on both axes so an unexpected aspect
        // ratio cannot produce conflicting size constraints.
        if (string.Equals(data.SignatureFormat, "svg", StringComparison.OrdinalIgnoreCase))
        {
            container.AlignMiddle().AlignCenter()
                .MaxHeight(22, Unit.Millimetre)
                .MaxWidth(60, Unit.Millimetre)
                .Svg(System.Text.Encoding.UTF8.GetString(data.Signature));
        }
        else
        {
            container.AlignMiddle().AlignCenter().Image(data.Signature).FitArea();
        }
    }

    /// <summary>
    /// Cheap sanity check before the bytes reach the renderer.
    ///
    /// A corrupt signature must not stop a hospital printing the record — the
    /// certificate still names who signed it — and because QuestPDF renders
    /// lazily, the only place to intervene is before the element is added.
    /// </summary>
    private bool IsRenderableSignature()
    {
        var bytes = data.Signature!;

        if (string.Equals(data.SignatureFormat, "svg", StringComparison.OrdinalIgnoreCase))
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            return text.Contains("<svg", StringComparison.OrdinalIgnoreCase)
                && text.Contains("</svg>", StringComparison.OrdinalIgnoreCase);
        }

        // PNG magic number.
        return bytes.Length > 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
    }

    private void Footer(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().PaddingTop(6).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);

            col.Item().PaddingTop(4).Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    // Says plainly that nobody hand-typed this, which is the
                    // question an auditor asks about a printed record.
                    left.Item().Text(
                            "Generated by Hospital PM from the maintenance record. " +
                            "The checklist version shown is the one completed.")
                        .FontSize(7).FontColor(Colors.Grey.Darken1);

                    if (!string.IsNullOrWhiteSpace(options.AccreditationReference))
                    {
                        left.Item().Text(options.AccreditationReference!)
                            .FontSize(7).FontColor(Colors.Grey.Darken1);
                    }
                });

                row.ConstantItem(40, Unit.Millimetre).AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(s => s.FontSize(7).FontColor(Colors.Grey.Darken1));
                    text.Span("Page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        });
    }

    private static void Field(ColumnDescriptor col, string label, string? value, bool bold = false)
    {
        col.Item().PaddingBottom(3).Row(row =>
        {
            row.ConstantItem(28, Unit.Millimetre)
                .Text(label).FontSize(8).FontColor(Colors.Grey.Darken1);

            var text = row.RelativeItem().Text(string.IsNullOrWhiteSpace(value) ? "—" : value!);
            if (bold)
            {
                text.Bold();
            }
        });
    }

    private static void HeaderCell(IContainer container, string text)
        => container.Background(Colors.Grey.Lighten2).Padding(4)
            .Text(text).FontSize(8).Bold().FontColor(Colors.Grey.Darken3);

    private static IContainer Cell(IContainer container)
        => container.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).Padding(4);
}
