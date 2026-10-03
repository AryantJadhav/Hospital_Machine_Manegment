using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace HospitalPm.Infrastructure.Reports;

public sealed record IncidentSummaryCount(string Label, int Count);

public sealed record IncidentSummaryRow(
    string Reference,
    DateOnly OccurredOn,
    string AssetTag,
    string? MachineName,
    string? Location,
    string Type,
    string Damage,
    string Status);

/// <summary>Everything the incident summary prints, read from the records when it is asked for.</summary>
public sealed record IncidentSummaryReportData(
    DateOnly? From,
    DateOnly? To,
    DateOnly GeneratedOn,
    int Total,
    int Open,
    int Closed,
    int TakenOutOfUse,
    IReadOnlyList<IncidentSummaryCount> ByType,
    IReadOnlyList<IncidentSummaryCount> ByDamage,
    IReadOnlyList<IncidentSummaryCount> ByLocation,
    IReadOnlyList<IncidentSummaryCount> RepeatMachines,
    IReadOnlyList<IncidentSummaryRow> Rows,
    bool Truncated);

/// <summary>
/// The incidents of a period at a glance: how many, of what kind, how badly the machines were left, where
/// they happened, and which machines come up again and again; then each incident on a line.
///
/// For the hospital's equipment-safety review and for an accreditation file. It is about machines, not
/// patients. Like the other reports it uses the bundled Lato font, so it looks the same on a Windows PC
/// and a minimal Linux server.
/// </summary>
public sealed class IncidentSummaryDocument(IncidentSummaryReportData data, ReportOptions options) : IDocument
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
                    right.Item().AlignRight().Text("EQUIPMENT INCIDENTS").FontSize(11).Bold().FontColor(Accent);
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
                Tile(row.RelativeItem(), "Incidents", data.Total);
                Tile(row.RelativeItem(), "Still open", data.Open);
                Tile(row.RelativeItem(), "Closed", data.Closed);
                Tile(row.RelativeItem(), "Taken out of use", data.TakenOutOfUse);
            });

            if (data.Total == 0)
            {
                col.Item().Text("No incident was recorded in this period.").FontColor(Colors.Grey.Darken1);
                return;
            }

            col.Item().Row(row =>
            {
                row.Spacing(12);
                row.RelativeItem().Element(c => Breakdown(c, "By kind", data.ByType, "None"));
                row.RelativeItem().Element(c => Breakdown(c, "By state the machine was left in", data.ByDamage, "None"));
            });

            col.Item().Row(row =>
            {
                row.Spacing(12);
                row.RelativeItem().Element(c => Breakdown(c, "Where (top places)", data.ByLocation, "None"));
                row.RelativeItem().Element(c => Breakdown(c, "Machines with more than one incident", data.RepeatMachines, "None"));
            });

            col.Item().Element(RowsBlock);
        });
    }

    private static void Tile(IContainer container, string label, int value)
    {
        container.Border(1).BorderColor(Colors.Grey.Lighten1).Padding(8).Column(c =>
        {
            c.Item().Text(value.ToString("N0", CultureInfo.InvariantCulture)).FontSize(18).Bold();
            c.Item().Text(label).FontSize(8.5f).FontColor(Colors.Grey.Darken1);
        });
    }

    private static void Breakdown(IContainer container, string title, IReadOnlyList<IncidentSummaryCount> counts, string none)
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
            col.Item().Text("Each incident").FontSize(10).Bold();

            col.Item().PaddingTop(3).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(28, Unit.Millimetre);   // reference
                    c.ConstantColumn(23, Unit.Millimetre);   // date
                    c.RelativeColumn(2.4f);                  // machine
                    c.RelativeColumn(1.6f);                  // kind
                    c.RelativeColumn(2.2f);                  // state
                    c.ConstantColumn(20, Unit.Millimetre);   // status
                });

                table.Header(h =>
                {
                    HeaderCell(h.Cell(), "Reference");
                    HeaderCell(h.Cell(), "Date");
                    HeaderCell(h.Cell(), "Machine");
                    HeaderCell(h.Cell(), "Kind");
                    HeaderCell(h.Cell(), "State left in");
                    HeaderCell(h.Cell(), "Status");
                });

                foreach (var r in data.Rows)
                {
                    table.Cell().Element(Cell).Text(r.Reference).FontSize(8.5f);
                    table.Cell().Element(Cell).Text(Date(r.OccurredOn)).FontSize(8.5f);
                    table.Cell().Element(Cell).Text(t =>
                    {
                        t.Span(r.AssetTag).Bold().FontSize(8.5f);
                        t.Span($" {r.MachineName}{(string.IsNullOrWhiteSpace(r.Location) ? string.Empty : " · " + r.Location)}").FontSize(8.5f);
                    });
                    table.Cell().Element(Cell).Text(r.Type).FontSize(8.5f);
                    table.Cell().Element(Cell).Text(r.Damage).FontSize(8.5f);
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
                    .Text("Generated by Hospital PM from the incident records. About machines only: no patient information is held here.")
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

    private static void HeaderCell(IContainer container, string text) =>
        container.Background(Colors.Grey.Lighten3).PaddingVertical(3).PaddingHorizontal(4)
            .Text(text).FontSize(8.5f).Bold();

    private static IContainer Cell(IContainer container) =>
        container.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3).PaddingHorizontal(4);
}
