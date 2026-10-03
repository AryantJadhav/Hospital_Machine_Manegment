using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace HospitalPm.Infrastructure.Reports;

public sealed record GatePassReportItem(string Description, string? AssetCode, int Quantity, string? Remarks);

/// <summary>Everything one gate pass prints, read from the record when it is asked for.</summary>
public sealed record GatePassReportData(
    int Number,
    DateOnly PassDate,
    string VendorName,
    string? ContactPerson,
    string? ContactPhone,
    string Purpose,
    string? RequestNumber,
    DateOnly? RequestDate,
    DateOnly? ExpectedReturnDate,
    DateOnly? ReturnedOn,
    bool IsCancelled,
    string? AuthorisedBy,
    string? PreparedBy,
    IReadOnlyList<GatePassReportItem> Items)
{
    public string Reference => $"GP-{Number}";

    public int TotalQuantity => Items.Sum(i => i.Quantity);
}

/// <summary>
/// The hospital's returnable gate pass, as the printed book has it: who the goods are going to and
/// why, the items with their asset codes and quantities, the day they are due back, and a place for
/// each of four people to sign.
///
/// The book has three copies (white for the vendor, pink for security, yellow for the department
/// that sent the machine), so this prints three pages, one for each and marked with whose it is.
/// Like the other reports it uses the bundled Lato font, so it looks the same on a Windows PC and a
/// minimal Linux server.
/// </summary>
public sealed class GatePassDocument(GatePassReportData data, ReportOptions options) : IDocument
{
    private const string BundledFont = "Lato";

    private static readonly string Accent = Colors.Blue.Darken2;

    private static readonly (string Name, string Colour, string Owner)[] Copies =
    [
        ("Vendor copy", "White", "Vendor"),
        ("Security copy", "Pink", "Security"),
        ("Department copy", "Yellow", "Concerned Dept."),
    ];

    public void Compose(IDocumentContainer container)
    {
        foreach (var copy in Copies)
        {
            var name = copy.Name;

            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(16, Unit.Millimetre);
                page.DefaultTextStyle(t => t.FontFamily(BundledFont).FontSize(10));

                page.Header().Element(h => Header(h, name));
                page.Content().Element(Content);
                page.Footer().Element(Footer);
            });
        }
    }

    private void Header(IContainer container, string copyName)
    {
        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().Text(string.IsNullOrWhiteSpace(options.HospitalName) ? "Hospital PM" : options.HospitalName)
                        .FontSize(15).Bold();

                    left.Item().PaddingTop(2).Text("Biomedical Engineering Department")
                        .FontSize(9).FontColor(Colors.Grey.Darken1);
                });

                row.ConstantItem(62, Unit.Millimetre).AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text(copyName.ToUpperInvariant()).FontSize(10).Bold().FontColor(Accent);

                    if (data.IsCancelled)
                    {
                        right.Item().AlignRight().Text("CANCELLED").FontSize(10).Bold().FontColor(Colors.Red.Darken2);
                    }
                });
            });

            col.Item().PaddingTop(8).AlignCenter().Text("Returnable Gate Pass").FontSize(16).Bold();
            col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Accent);
        });
    }

    private void Content(IContainer container)
    {
        container.PaddingVertical(10).Column(col =>
        {
            col.Spacing(12);

            col.Item().Element(DetailsBlock);
            col.Item().Element(ItemsBlock);

            if (!string.IsNullOrWhiteSpace(data.AuthorisedBy))
            {
                col.Item().Text(t =>
                {
                    t.Span("Authorised by: ").FontColor(Colors.Grey.Darken1);
                    t.Span(data.AuthorisedBy!).Bold();
                });
            }

            col.Item().Element(SignOffBlock);
        });
    }

    private void DetailsBlock(IContainer container)
    {
        container.Border(1).BorderColor(Colors.Grey.Darken1).Padding(8).Column(col =>
        {
            col.Spacing(5);

            col.Item().Row(row =>
            {
                row.RelativeItem().Element(c => Field(c, "Gate pass no.", data.Reference, bold: true));
                row.RelativeItem().Element(c => Field(c, "Date", Date(data.PassDate)));
            });

            col.Item().Row(row =>
            {
                row.RelativeItem().Element(c => Field(c, "Request no.", data.RequestNumber));
                row.RelativeItem().Element(c => Field(c, "Request date", Date(data.RequestDate)));
            });

            col.Item().Element(c => Field(c, "Name of company / person", Company(), bold: true));
            col.Item().Element(c => Field(c, "Purpose", data.Purpose));

            col.Item().Row(row =>
            {
                row.RelativeItem().Element(c => Field(c, "Expected date of return", Date(data.ExpectedReturnDate)));
                // Left to be written in by hand at the gate when the goods have not come back yet.
                row.RelativeItem().Element(c => Field(c, "Actual date of return", Date(data.ReturnedOn), blankLine: data.ReturnedOn is null));
            });
        });
    }

    private void ItemsBlock(IContainer container)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(12, Unit.Millimetre);   // sr. no.
                c.RelativeColumn(4.2f);                  // description
                c.RelativeColumn(1.8f);                  // asset code
                c.ConstantColumn(18, Unit.Millimetre);   // quantity
                c.RelativeColumn(2.6f);                  // remarks
            });

            table.Header(h =>
            {
                HeaderCell(h.Cell(), "Sr. No.");
                HeaderCell(h.Cell(), "Description");
                HeaderCell(h.Cell(), "Asset code");
                HeaderCell(h.Cell(), "Quantity");
                HeaderCell(h.Cell(), "Remarks");
            });

            var n = 0;
            foreach (var item in data.Items)
            {
                n++;
                table.Cell().Element(Cell).Text(n.ToString(CultureInfo.InvariantCulture));
                table.Cell().Element(Cell).Text(item.Description);
                // The paper puts NA for something with no number of its own.
                table.Cell().Element(Cell).Text(string.IsNullOrWhiteSpace(item.AssetCode) ? "NA" : item.AssetCode);
                table.Cell().Element(Cell).Text(item.Quantity.ToString(CultureInfo.InvariantCulture));
                table.Cell().Element(Cell).Text(item.Remarks ?? string.Empty);
            }

            table.Cell().ColumnSpan(3).Element(Cell).AlignRight().Text("Total").Bold();
            table.Cell().Element(Cell).Text(data.TotalQuantity.ToString(CultureInfo.InvariantCulture)).Bold();
            table.Cell().Element(Cell).Text(string.Empty);
        });
    }

    private void SignOffBlock(IContainer container)
    {
        container.PaddingTop(14).Row(row =>
        {
            row.Spacing(8);
            SignatureLine(row.RelativeItem(), "Prepared by", data.PreparedBy);
            SignatureLine(row.RelativeItem(), "Authorised sign", null);
            SignatureLine(row.RelativeItem(), "Security officer / supervisor sign", null);
            SignatureLine(row.RelativeItem(), "Vendor sign", null);
        });
    }

    private static void SignatureLine(IContainer container, string role, string? name)
    {
        container.Column(c =>
        {
            c.Item().Text(role).FontSize(8).FontColor(Colors.Grey.Darken1);
            c.Item().PaddingTop(20).LineHorizontal(0.5f).LineColor(Colors.Grey.Darken1);
            c.Item().PaddingTop(2).Text(string.IsNullOrWhiteSpace(name) ? "Name, sign and date" : name)
                .FontSize(7.5f).FontColor(Colors.Grey.Darken1);
        });
    }

    private void Footer(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().PaddingTop(6).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);

            col.Item().PaddingTop(4).Row(row =>
            {
                row.RelativeItem().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(7.5f).FontColor(Colors.Grey.Darken1));
                    t.Span("Note: White copy - Vendor  •  Pink copy - Security  •  Yellow copy - Concerned Dept.");
                });

                row.ConstantItem(50, Unit.Millimetre).AlignRight()
                    .Text($"{data.Reference} · generated by Hospital PM")
                    .FontSize(7).FontColor(Colors.Grey.Darken1);
            });
        });
    }

    private string Company()
    {
        var who = string.Join(" - ", new[] { data.VendorName, data.ContactPerson, data.ContactPhone }
            .Where(p => !string.IsNullOrWhiteSpace(p)));
        return who;
    }

    private static string? Date(DateOnly? date) =>
        date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static void Field(IContainer container, string label, string? value, bool bold = false, bool blankLine = false)
    {
        container.Row(row =>
        {
            row.AutoItem().PaddingRight(5).Text(label + ":").FontSize(9).FontColor(Colors.Grey.Darken2);

            var cell = row.RelativeItem();
            if (blankLine || string.IsNullOrWhiteSpace(value))
            {
                // A line to write on, as the paper has.
                cell.PaddingTop(10).LineHorizontal(0.5f).LineColor(Colors.Grey.Medium);
                return;
            }

            var text = cell.Text(value);
            if (bold)
            {
                text.Bold();
            }
        });
    }

    private static void HeaderCell(IContainer container, string text) =>
        container.Border(0.5f).BorderColor(Colors.Grey.Darken1).Background(Colors.Grey.Lighten3)
            .PaddingVertical(4).PaddingHorizontal(4).Text(text).FontSize(9).Bold();

    private static IContainer Cell(IContainer container) =>
        container.Border(0.5f).BorderColor(Colors.Grey.Darken1)
            .PaddingVertical(5).PaddingHorizontal(4).MinHeight(8, Unit.Millimetre);
}
