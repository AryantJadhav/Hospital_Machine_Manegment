using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace HospitalPm.Infrastructure.Reports;

public sealed record TrainingReportAttendee(string Name, string? Designation);

/// <summary>Everything one training report prints, read from the record when it is asked for.</summary>
public sealed record TrainingReportData(
    int SessionId,
    string Title,
    DateOnly SessionDate,
    bool IsPlanned,
    string? AssetTag,
    string? MachineName,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? Location,
    string? Trainer,
    string? Venue,
    int? DurationMinutes,
    string? Notes,
    string? RecordedBy,
    IReadOnlyList<TrainingReportAttendee> Attendees)
{
    /// <summary>What to quote: TR-2026-00012.</summary>
    public string Reference => ReferenceFor(SessionDate, SessionId);

    /// <summary>The same reference for a session that has no report built yet, so the screen and the paper agree.</summary>
    public static string ReferenceFor(DateOnly sessionDate, int sessionId) => $"TR-{sessionDate.Year}-{sessionId:D5}";
}

/// <summary>
/// The record of one training session: which machine it was on, who ran it, who attended, and a
/// column for each person to sign against their own name.
///
/// Printed for the file an accreditation assessor asks to see. Like the other reports it uses the
/// bundled Lato font, so it looks the same on a Windows PC and a minimal Linux server.
/// </summary>
public sealed class TrainingReportDocument(TrainingReportData data, ReportOptions options) : IDocument
{
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
                    left.Item().Text(string.IsNullOrWhiteSpace(options.HospitalName) ? "Hospital PM" : options.HospitalName)
                        .FontSize(15).Bold();

                    left.Item().PaddingTop(2).Text("Biomedical Engineering Department")
                        .FontSize(9).FontColor(Colors.Grey.Darken1);
                });

                row.ConstantItem(58, Unit.Millimetre).AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text("TRAINING REPORT").FontSize(11).Bold().FontColor(Accent);
                    right.Item().AlignRight().Text(data.Reference).FontSize(11).Bold();
                    right.Item().AlignRight().Text(data.IsPlanned ? "Planned" : "Held")
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

            col.Item().Element(MachineBlock);
            col.Item().Element(SessionBlock);

            if (!string.IsNullOrWhiteSpace(data.Notes))
            {
                col.Item().Column(notes =>
                {
                    notes.Item().Text("Notes").FontSize(11).Bold();
                    notes.Item().PaddingTop(3).Background(Colors.Grey.Lighten4).Padding(6).Text(data.Notes!);
                });
            }

            col.Item().Element(AttendeesBlock);
            col.Item().Element(SignOffBlock);
        });
    }

    private void MachineBlock(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().Text("Machine").FontSize(11).Bold();

            if (data.AssetTag is null)
            {
                col.Item().PaddingTop(3).Text("Not about one machine.").FontColor(Colors.Grey.Darken1);
                return;
            }

            col.Item().PaddingTop(3).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(8).Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    Field(left, "Machine number", data.AssetTag, bold: true);
                    Field(left, "Machine", data.MachineName);
                    Field(left, "Location", data.Location);
                });

                row.RelativeItem().Column(right =>
                {
                    Field(right, "Company", data.Manufacturer);
                    Field(right, "Model", data.Model);
                    Field(right, "Serial number", data.SerialNumber);
                });
            });
        });
    }

    private void SessionBlock(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().Text("Training").FontSize(11).Bold();

            col.Item().PaddingTop(3).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(8).Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    Field(left, "Session", data.Title, bold: true);
                    Field(left, "Date", data.SessionDate.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
                    Field(left, "Length", Length(data.DurationMinutes));
                });

                row.RelativeItem().Column(right =>
                {
                    Field(right, "Trainer", data.Trainer);
                    Field(right, "Where", data.Venue);
                    Field(right, "Recorded by", data.RecordedBy);
                });
            });
        });
    }

    private void AttendeesBlock(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().Text($"{(data.IsPlanned ? "Expected" : "Attended")} ({data.Attendees.Count})").FontSize(11).Bold();

            if (data.Attendees.Count == 0)
            {
                col.Item().PaddingTop(3).Text(data.IsPlanned ? "No one has been listed yet." : "No one was listed for this session.")
                    .FontColor(Colors.Grey.Darken1);
                return;
            }

            col.Item().PaddingTop(3).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(10, Unit.Millimetre);   // number
                    c.RelativeColumn(3.2f);                  // name
                    c.RelativeColumn(2.6f);                  // job
                    c.RelativeColumn(3);                     // signature
                });

                table.Header(h =>
                {
                    HeaderCell(h.Cell(), "No.");
                    HeaderCell(h.Cell(), "Name");
                    HeaderCell(h.Cell(), "Job");
                    HeaderCell(h.Cell(), "Signature");
                });

                var n = 0;
                foreach (var a in data.Attendees)
                {
                    n++;
                    table.Cell().Element(Cell).Text(n.ToString(CultureInfo.InvariantCulture));
                    table.Cell().Element(Cell).Text(a.Name);
                    table.Cell().Element(Cell).Text(a.Designation ?? "—");
                    // Left blank on purpose: each person signs against their own name on paper.
                    table.Cell().Element(Cell).MinHeight(9, Unit.Millimetre);
                }
            });
        });
    }

    private void SignOffBlock(IContainer container)
    {
        container.PaddingTop(10).Row(row =>
        {
            SignatureLine(row.RelativeItem(), "Trainer");
            row.ConstantItem(14, Unit.Millimetre);
            SignatureLine(row.RelativeItem(), "Biomedical engineering head");
        });
    }

    private static void SignatureLine(IContainer container, string role)
    {
        container.Column(c =>
        {
            c.Item().Text(role).FontSize(8).FontColor(Colors.Grey.Darken1);
            c.Item().PaddingTop(16).LineHorizontal(0.5f).LineColor(Colors.Grey.Darken1);
            c.Item().PaddingTop(2).Text("Name, signature and date").FontSize(7).FontColor(Colors.Grey.Medium);
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
                    .Text("Generated by Hospital PM from the training record.")
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

    private static string? Length(int? minutes)
    {
        if (minutes is not { } m)
        {
            return null;
        }

        var h = m / 60;
        var rest = m % 60;
        return h == 0 ? $"{rest} min" : rest == 0 ? $"{h} h" : $"{h} h {rest} min";
    }

    private static void Field(ColumnDescriptor col, string label, string? value, bool bold = false)
    {
        col.Item().PaddingBottom(3).Row(row =>
        {
            row.ConstantItem(30, Unit.Millimetre).Text(label).FontSize(8.5f).FontColor(Colors.Grey.Darken1);

            var text = row.RelativeItem().Text(string.IsNullOrWhiteSpace(value) ? "—" : value);
            if (bold)
            {
                text.Bold();
            }
        });
    }

    private static void HeaderCell(IContainer container, string text) =>
        container.Background(Colors.Grey.Lighten3).PaddingVertical(3).PaddingHorizontal(4)
            .Text(text).FontSize(8.5f).Bold();

    private static IContainer Cell(IContainer container) =>
        container.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3).PaddingHorizontal(4);
}
