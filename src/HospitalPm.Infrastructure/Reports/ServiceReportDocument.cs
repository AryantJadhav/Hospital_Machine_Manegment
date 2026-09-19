using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace HospitalPm.Infrastructure.Reports;

/// <summary>
/// The record of one breakdown and its repair.
///
/// Prints the timeline as well as the outcome. "Replaced flow sensor" alone
/// tells a hospital nothing about why the machine was down for three days;
/// the timeline shows the part was on order, which is the fact that changes
/// a purchasing decision.
/// </summary>
public sealed class ServiceReportDocument(
    ServiceReportData data,
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

                    left.Item().PaddingTop(2).Text("Biomedical Engineering Department")
                        .FontSize(9).FontColor(Colors.Grey.Darken1);
                });

                row.ConstantItem(58, Unit.Millimetre).AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text("SERVICE REPORT")
                        .FontSize(11).Bold().FontColor(Accent);
                    right.Item().AlignRight().Text(data.Number).FontSize(11).Bold();
                    right.Item().AlignRight().Text(data.Status)
                        .FontSize(8.5f).FontColor(Colors.Grey.Darken2);
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

            col.Item().Border(1).BorderColor(Colors.Grey.Lighten1).Padding(8).Row(row =>
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
                    Field(right, "Priority", data.Priority);
                    Field(right, "Reported by", data.ReportedByName);
                });
            });

            col.Item().Element(FaultBlock);
            col.Item().Element(DowntimeBlock);

            if (data.Timeline.Count > 0)
            {
                col.Item().Element(TimelineBlock);
            }

            col.Item().Element(SignOffBlock);
        });
    }

    private void FaultBlock(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().Text("Reported fault").FontSize(11).Bold();
            col.Item().PaddingTop(3).Background(Colors.Grey.Lighten4).Padding(6)
                .Text(data.FaultDescription);

            col.Item().PaddingTop(8).Text("Work carried out").FontSize(11).Bold();
            col.Item().PaddingTop(3).Background(Colors.Grey.Lighten4).Padding(6)
                .Text(string.IsNullOrWhiteSpace(data.ResolutionNotes)
                    ? "Not yet resolved."
                    : data.ResolutionNotes!);
        });
    }

    private void DowntimeBlock(IContainer container)
    {
        container.Row(row =>
        {
            Stat(row, "Reported", Local(data.ReportedAtUtc));
            Stat(row, "Work started", Local(data.StartedAtUtc));
            Stat(row, "Resolved", Local(data.ResolvedAtUtc));

            // The number a hospital is actually asked for at an audit, and
            // the reason out-of-service time is recorded separately from
            // status changes.
            Stat(row, "Downtime", data.DowntimeMinutes is { } m ? Duration(m) : "—", emphasise: true);
        });
    }

    private void TimelineBlock(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().PaddingBottom(4).Text("Timeline").FontSize(11).Bold();

            foreach (var (at, author, body) in data.Timeline)
            {
                col.Item().PaddingBottom(4).Row(row =>
                {
                    row.ConstantItem(30, Unit.Millimetre)
                        .Text(ReportTime.DayAndTime(at, utcOffset))
                        .FontSize(8).FontColor(Colors.Grey.Darken1);

                    row.ConstantItem(28, Unit.Millimetre)
                        .Text(author).FontSize(8).FontColor(Colors.Grey.Darken1);

                    row.RelativeItem().Text(body).FontSize(9);
                });
            }
        });
    }

    private void SignOffBlock(IContainer container)
    {
        container.PaddingTop(6).Row(row =>
        {
            row.RelativeItem().Column(left =>
            {
                Field(left, "Resolved by", data.ResolvedByName);
                Field(left, "Back in service", Local(data.BackInServiceAtUtc));
            });

            row.ConstantItem(60, Unit.Millimetre).Column(right =>
            {
                // Left blank on purpose: a service report is countersigned by
                // the ward accepting the machine back, and that happens on
                // paper at the bedside.
                right.Item().Text("Accepted by (ward)").FontSize(8)
                    .FontColor(Colors.Grey.Darken1);
                right.Item().PaddingTop(16).LineHorizontal(0.5f).LineColor(Colors.Grey.Darken1);
                right.Item().PaddingTop(2).Text("Name, signature and date")
                    .FontSize(7).FontColor(Colors.Grey.Medium);
            });
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
                    .Text("Generated by Hospital PM from the work order record.")
                    .FontSize(7).FontColor(Colors.Grey.Darken1);

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

    private static void Stat(RowDescriptor row, string label, string value, bool emphasise = false)
    {
        row.RelativeItem().Border(1).BorderColor(Colors.Grey.Lighten1).Padding(6).Column(col =>
        {
            col.Item().Text(label).FontSize(8).FontColor(Colors.Grey.Darken1);
            var text = col.Item().Text(value).FontSize(emphasise ? 12 : 10);
            if (emphasise)
            {
                text.Bold();
            }
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

    private string Local(DateTime? utc)
        => utc is null ? "—" : ReportTime.DateTime(utc.Value, utcOffset);

    /// <summary>Hours and minutes, because "4380 minutes" means nothing to a reader.</summary>
    private static string Duration(int minutes)
    {
        if (minutes < 60)
        {
            return $"{minutes} min";
        }

        var days = minutes / 1440;
        var hours = (minutes % 1440) / 60;
        var mins = minutes % 60;

        return days > 0 ? $"{days}d {hours}h" : $"{hours}h {mins}m";
    }
}
