using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace HospitalPm.Infrastructure.Reports;

/// <summary>Everything one incident report prints, read from the record when it is asked for.</summary>
public sealed record IncidentReportData(
    string Reference,
    string StatusLabel,
    string? AssetTag,
    string? MachineName,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? Location,
    string TypeLabel,
    DateOnly OccurredOn,
    TimeOnly? OccurredAt,
    string? Place,
    string Description,
    string? InvolvedPerson,
    string? ImmediateAction,
    bool TakenOutOfUse,
    string DamageLabel,
    string? Findings,
    string? CorrectiveAction,
    string? ReportedBy,
    DateTime ReportedAtUtc,
    string? ClosedBy,
    DateTime? ClosedAtUtc);

/// <summary>
/// The record of one incident with a machine: what it was, when and where, what happened and what was done
/// at once, the state the machine was left in, what the biomedical team found, and what is being done so
/// it does not happen again. With a place for each person to sign.
///
/// Printed for the file an accreditation assessor or an insurer asks to see. It is about the machine and
/// names no patient. Like the other reports it uses the bundled Lato font, so it looks the same on a
/// Windows PC and a minimal Linux server.
/// </summary>
public sealed class IncidentReportDocument(IncidentReportData data, ReportOptions options, TimeSpan utcOffset = default) : IDocument
{
    private const string BundledFont = "Lato";

    private static readonly string Accent = Colors.Red.Darken3;

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

                row.ConstantItem(62, Unit.Millimetre).AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text("EQUIPMENT INCIDENT REPORT").FontSize(11).Bold().FontColor(Accent);
                    right.Item().AlignRight().Text(data.Reference).FontSize(11).Bold();
                    right.Item().AlignRight().Text(data.StatusLabel).FontSize(8.5f).FontColor(Colors.Grey.Darken2);
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
            col.Item().Element(WhatHappenedBlock);
            col.Item().Element(ReviewBlock);
            col.Item().Element(SignOffBlock);
        });
    }

    private void MachineBlock(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().Text("Machine").FontSize(11).Bold();

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

    private void WhatHappenedBlock(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().Text("What happened").FontSize(11).Bold();

            col.Item().PaddingTop(3).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(8).Column(box =>
            {
                box.Item().Row(row =>
                {
                    row.RelativeItem().Column(left =>
                    {
                        Field(left, "Kind", data.TypeLabel, bold: true);
                        Field(left, "Date", data.OccurredOn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
                        Field(left, "Time", data.OccurredAt?.ToString("HH:mm", CultureInfo.InvariantCulture));
                    });

                    row.RelativeItem().Column(right =>
                    {
                        Field(right, "Where", data.Place);
                        Field(right, "Staff involved", data.InvolvedPerson);
                        Field(right, "Taken out of use", data.TakenOutOfUse ? "Yes" : "No");
                    });
                });

                box.Item().PaddingTop(4).Element(c => Paragraph(c, "Description", data.Description));
                box.Item().PaddingTop(6).Element(c => Paragraph(c, "Done straight away", data.ImmediateAction));
                box.Item().PaddingTop(6).Text(t =>
                {
                    t.Span("State of the machine: ").FontColor(Colors.Grey.Darken1);
                    t.Span(data.DamageLabel).Bold();
                });
            });
        });
    }

    private void ReviewBlock(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().Text("Review by the biomedical team").FontSize(11).Bold();

            col.Item().PaddingTop(3).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(8).Column(box =>
            {
                if (string.IsNullOrWhiteSpace(data.Findings) && string.IsNullOrWhiteSpace(data.CorrectiveAction))
                {
                    box.Item().Text("Not reviewed yet.").FontColor(Colors.Grey.Darken1);
                    return;
                }

                box.Item().Element(c => Paragraph(c, "What caused it", data.Findings));
                box.Item().PaddingTop(6).Element(c => Paragraph(c, "What is being done so it does not happen again", data.CorrectiveAction));
            });
        });
    }

    private void SignOffBlock(IContainer container)
    {
        container.PaddingTop(10).Column(col =>
        {
            col.Item().Row(row =>
            {
                SignatureLine(row.RelativeItem(), "Reported by", data.ReportedBy, ReportTime.DateTime(data.ReportedAtUtc, utcOffset));
                row.ConstantItem(8, Unit.Millimetre);
                SignatureLine(row.RelativeItem(), "Reviewed by (biomedical engineer)", data.ClosedBy,
                    data.ClosedAtUtc is { } closed ? ReportTime.DateTime(closed, utcOffset) : null);
                row.ConstantItem(8, Unit.Millimetre);
                SignatureLine(row.RelativeItem(), "Head of the department", null, null);
            });
        });
    }

    private static void SignatureLine(IContainer container, string role, string? name, string? when)
    {
        container.Column(c =>
        {
            c.Item().Text(role).FontSize(8).FontColor(Colors.Grey.Darken1);
            c.Item().PaddingTop(18).LineHorizontal(0.5f).LineColor(Colors.Grey.Darken1);
            c.Item().PaddingTop(2).Text(string.IsNullOrWhiteSpace(name) ? "Name, signature and date" : $"{name}{(when is null ? string.Empty : " · " + when)}")
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
                row.RelativeItem()
                    .Text($"Generated by Hospital PM from the incident record. About the machine only: no patient information is held here. Times are {ReportTime.Zone(utcOffset)}.")
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

    private static void Paragraph(IContainer container, string label, string? value)
    {
        container.Column(c =>
        {
            c.Item().Text(label).FontSize(8.5f).FontColor(Colors.Grey.Darken1);
            c.Item().PaddingTop(1).Text(string.IsNullOrWhiteSpace(value) ? "—" : value);
        });
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
}
