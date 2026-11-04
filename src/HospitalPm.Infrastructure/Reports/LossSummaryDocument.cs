using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace HospitalPm.Infrastructure.Reports;

public sealed record LossSummaryCount(string Label, int Count);

public sealed record LossSummaryRow(
    string Reference,
    DateOnly NoticedOn,
    string AssetTag,
    string? MachineName,
    string? Location,
    string Kind,
    decimal? Value,
    string Status);

/// <summary>Everything the loss summary prints, read from the records when it is asked for.</summary>
public sealed record LossSummaryReportData(
    DateOnly? From,
    DateOnly? To,
    DateOnly GeneratedOn,
    int Total,
    int Open,
    int Recovered,
    int WrittenOff,
    // What the machines were worth: all of them, what is still missing or accepted as lost, and what came back.
    decimal ValueReported,
    decimal ValueLost,
    decimal ValueRecovered,
    // Lost machines whose value nobody recorded, so the figures above are not mistaken for the whole.
    int WithoutValue,
    IReadOnlyList<LossSummaryCount> ByKind,
    IReadOnlyList<LossSummaryCount> ByLocation,
    IReadOnlyList<LossSummaryCount> RepeatMachines,
    IReadOnlyList<LossSummaryRow> Rows,
    bool Truncated);

/// <summary>
/// The machines lost in a period at a glance: how many, how they went, what they were worth, how many came back and how many
/// were written off, where they went from and which machines go missing again and again; then each loss on a line.
///
/// For the hospital's asset review, its insurer and an audit file. It is about machines, not patients. Like the other
/// reports it uses the bundled Lato font, so it looks the same on a Windows PC and a minimal Linux server.
/// </summary>
public sealed class LossSummaryDocument(LossSummaryReportData data, ReportOptions options) : IDocument
{
    private const string BundledFont = "Lato";

    private static readonly string Accent = Colors.Red.Darken3;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(16, Unit.Millimetre);
            page.DefaultTextStyle(t => t.FontFamily(BundledFont).FontSize(9.5f));

            page.Header().Element(Header);
            page.Content().Element(Content);
            page.Footer().Element(Footer);
        });
    }

    private string Period() => (data.From, data.To) switch
    {
        ({ } f, { } t) => $"{Date(f)} to {Date(t)}",
        ({ } f, null) => $"From {Date(f)}",
        (null, { } t) => $"Up to {Date(t)}",
        _ => "All time",
    };

    private void Header(IContainer container)
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

                row.ConstantItem(70, Unit.Millimetre).AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text("LOSS OF EQUIPMENT").FontSize(11).Bold().FontColor(Accent);
                    right.Item().AlignRight().Text(Period()).FontSize(10).Bold();
                    right.Item().AlignRight().Text($"Printed {Date(data.GeneratedOn)}").FontSize(8.5f).FontColor(Colors.Grey.Darken2);
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

            col.Item().Row(row =>
            {
                row.Spacing(8);
                Tile(row.RelativeItem(), "Machines lost", data.Total.ToString("N0", CultureInfo.InvariantCulture));
                Tile(row.RelativeItem(), "Still missing", data.Open.ToString("N0", CultureInfo.InvariantCulture));
                Tile(row.RelativeItem(), "Found again", data.Recovered.ToString("N0", CultureInfo.InvariantCulture));
                Tile(row.RelativeItem(), "Written off", data.WrittenOff.ToString("N0", CultureInfo.InvariantCulture));
            });

            if (data.Total == 0)
            {
                col.Item().Text("No machine was recorded as lost in this period.").FontColor(Colors.Grey.Darken1);
                return;
            }

            col.Item().Row(row =>
            {
                row.Spacing(8);
                Tile(row.RelativeItem(), "Worth, all reported", Rupees(data.ValueReported));
                Tile(row.RelativeItem(), "Worth, not recovered", Rupees(data.ValueLost));
                Tile(row.RelativeItem(), "Worth, recovered", Rupees(data.ValueRecovered));
            });

            if (data.WithoutValue > 0)
            {
                var count = data.WithoutValue.ToString(CultureInfo.InvariantCulture);
                col.Item().Text($"{count} {(data.WithoutValue == 1 ? "machine has" : "machines have")} no value recorded, so the amounts above are at least this much.")
                    .FontSize(8.5f).FontColor(Colors.Grey.Darken1);
            }

            col.Item().Row(row =>
            {
                row.Spacing(12);
                row.RelativeItem().Element(c => Breakdown(c, "How they were lost", data.ByKind, "None"));
                row.RelativeItem().Element(c => Breakdown(c, "Where they went from (top places)", data.ByLocation, "None"));
            });

            col.Item().Element(c => Breakdown(c, "Machines lost more than once", data.RepeatMachines, "None"));

            col.Item().Element(RowsBlock);
            col.Item().Element(SignOffBlock);
        });
    }

    /// <summary>For the file: who prepared the report, and the head of Biomedical's signature on it.</summary>
    private static void SignOffBlock(IContainer container)
    {
        container.PaddingTop(14).Row(row =>
        {
            row.Spacing(12);
            SignatureLine(row.RelativeItem(), "Prepared by");
            SignatureLine(row.RelativeItem(), "Signature of the Head of Biomedical");
        });
    }

    private static void SignatureLine(IContainer container, string role)
    {
        container.Column(c =>
        {
            c.Item().Height(16, Unit.Millimetre);
            c.Item().LineHorizontal(0.7f).LineColor(Colors.Grey.Darken2);
            c.Item().PaddingTop(2).Text($"{role}: name, signature, date").FontSize(8).FontColor(Colors.Grey.Darken1);
        });
    }

    private static void Tile(IContainer container, string label, string value)
    {
        container.Border(1).BorderColor(Colors.Grey.Lighten1).Padding(8).Column(c =>
        {
            c.Item().Text(value).FontSize(16).Bold();
            c.Item().Text(label).FontSize(8.5f).FontColor(Colors.Grey.Darken1);
        });
    }

    private static void Breakdown(IContainer container, string title, IReadOnlyList<LossSummaryCount> counts, string none)
    {
        container.Column(col =>
        {
            col.Item().Text(title).FontSize(10).Bold();

            if (counts.Count == 0)
            {
                col.Item().PaddingTop(3).Text(none).FontColor(Colors.Grey.Darken1);
                return;
            }

            col.Item().PaddingTop(3).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn();
                    c.ConstantColumn(14, Unit.Millimetre);
                });

                foreach (var count in counts)
                {
                    table.Cell().Element(Cell).Text(count.Label);
                    table.Cell().Element(Cell).AlignRight().Text(count.Count.ToString(CultureInfo.InvariantCulture)).Bold();
                }
            });
        });
    }

    private void RowsBlock(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().Text("Each loss").FontSize(10).Bold();

            col.Item().PaddingTop(3).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(28, Unit.Millimetre);   // reference
                    c.ConstantColumn(23, Unit.Millimetre);   // date
                    c.RelativeColumn(2.6f);                  // machine
                    c.RelativeColumn(1.5f);                  // kind
                    c.ConstantColumn(22, Unit.Millimetre);   // value
                    c.RelativeColumn(1.6f);                  // status
                });

                table.Header(h =>
                {
                    HeaderCell(h.Cell(), "Reference");
                    HeaderCell(h.Cell(), "Found missing");
                    HeaderCell(h.Cell(), "Machine");
                    HeaderCell(h.Cell(), "Kind");
                    HeaderCell(h.Cell(), "Value");
                    HeaderCell(h.Cell(), "Status");
                });

                foreach (var r in data.Rows)
                {
                    table.Cell().Element(Cell).Text(r.Reference).FontSize(8.5f);
                    table.Cell().Element(Cell).Text(Date(r.NoticedOn)).FontSize(8.5f);
                    table.Cell().Element(Cell).Text(t =>
                    {
                        t.Span(r.AssetTag).Bold().FontSize(8.5f);
                        t.Span($" {r.MachineName}{(string.IsNullOrWhiteSpace(r.Location) ? string.Empty : " · " + r.Location)}").FontSize(8.5f);
                    });
                    table.Cell().Element(Cell).Text(r.Kind).FontSize(8.5f);
                    table.Cell().Element(Cell).AlignRight().Text(r.Value is { } v ? Rupees(v) : "—").FontSize(8.5f);
                    table.Cell().Element(Cell).Text(r.Status).FontSize(8.5f);
                }
            });

            if (data.Truncated)
            {
                col.Item().PaddingTop(4).Text($"The first {data.Rows.Count.ToString(CultureInfo.InvariantCulture)} are listed; narrow the period to see the rest.")
                    .FontSize(8).FontColor(Colors.Grey.Darken1);
            }
        });
    }

    private void Footer(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().PaddingTop(6).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);

            col.Item().PaddingTop(4).Row(row =>
            {
                row.RelativeItem()
                    .Text("Generated by Hospital PM from the loss records. About machines only: no patient information is held here. Records written in error are left out.")
                    .FontSize(7).FontColor(Colors.Grey.Darken1);

                row.ConstantItem(30, Unit.Millimetre).AlignRight().Text(text =>
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

    private static string Date(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Rupees(decimal amount) => string.Create(CultureInfo.InvariantCulture, $"₹{amount:N0}");

    private static void HeaderCell(IContainer container, string text) =>
        container.Background(Colors.Grey.Lighten3).PaddingVertical(3).PaddingHorizontal(4)
            .Text(text).FontSize(8.5f).Bold();

    private static IContainer Cell(IContainer container) =>
        container.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3).PaddingHorizontal(4);
}
