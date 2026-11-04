using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace HospitalPm.Infrastructure.Reports;

/// <summary>Everything one loss report prints, read from the record when it is asked for.</summary>
public sealed record LossReportData(
    string Reference,
    string StatusLabel,
    string? AssetTag,
    string? MachineName,
    string? Manufacturer,
    string? Model,
    string? SerialNumber,
    string? Location,
    string KindLabel,
    DateOnly NoticedOn,
    DateOnly? LastSeenOn,
    string? LastSeenPlace,
    string Description,
    string? LastHandledBy,
    string? PoliceReportNumber,
    DateOnly? PoliceReportedOn,
    decimal? EstimatedValue,
    string? Findings,
    string? CorrectiveAction,
    DateOnly? RecoveredOn,
    string? ReportedBy,
    DateTime ReportedAtUtc,
    string? ClosedBy,
    DateTime? ClosedAtUtc,
    // The biomedical engineer who approved it for the head, and the head's own words with the decision.
    string? ApprovedBy = null,
    DateTime? ApprovedAtUtc = null,
    string? DecisionNotes = null);

/// <summary>
/// The record of one machine that was lost: which machine, how and when it went and where it was last seen, whether the
/// police were told, what it was worth, what the biomedical team found and what is being done so it does not happen
/// again, and how it ended. With a place for each of the three who handle it to sign: the one who reported it, the biomedical
/// engineer who approved it, and the head of Biomedical who made the final decision.
///
/// Printed for the file an insurer, an auditor or a head of the hospital asks to see. It is about the machine and names no
/// patient. Like the other reports it uses the bundled Lato font, so it looks the same on a Windows PC and a minimal
/// Linux server.
/// </summary>
public sealed class LossReportDocument(LossReportData data, ReportOptions options, TimeSpan utcOffset = default) : IDocument
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

                row.ConstantItem(66, Unit.Millimetre).AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text("LOSS OF EQUIPMENT REPORT").FontSize(11).Bold().FontColor(Accent);
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
            col.Item().Element(PoliceBlock);
            col.Item().Element(ReviewBlock);
            col.Item().Element(SignOffBlock);
            col.Item().Element(DecisionBlock);
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
                    Field(left, "Meant to be in", data.Location);
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
                        Field(left, "Kind", data.KindLabel, bold: true);
                        Field(left, "Found missing on", Date(data.NoticedOn));
                        Field(left, "Last seen on", data.LastSeenOn is { } seen ? Date(seen) : null);
                    });

                    row.RelativeItem().Column(right =>
                    {
                        Field(right, "Last seen at", data.LastSeenPlace);
                        Field(right, "Last handled by", data.LastHandledBy);
                        Field(right, "Value", data.EstimatedValue is { } v ? Rupees(v) : null, bold: true);
                    });
                });

                box.Item().PaddingTop(4).Element(c => Paragraph(c, "Description", data.Description));
            });
        });
    }

    private void PoliceBlock(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().Text("Police complaint").FontSize(11).Bold();

            col.Item().PaddingTop(3).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(8).Row(row =>
            {
                if (string.IsNullOrWhiteSpace(data.PoliceReportNumber) && data.PoliceReportedOn is null)
                {
                    row.RelativeItem().Text("No police complaint is recorded.").FontColor(Colors.Grey.Darken1);
                    return;
                }

                row.RelativeItem().Column(left => Field(left, "Complaint (FIR) number", data.PoliceReportNumber, bold: true));
                row.RelativeItem().Column(right => Field(right, "Reported on", data.PoliceReportedOn is { } on ? Date(on) : null));
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
                if (string.IsNullOrWhiteSpace(data.Findings) && string.IsNullOrWhiteSpace(data.CorrectiveAction) && data.RecoveredOn is null)
                {
                    box.Item().Text("Not looked into yet.").FontColor(Colors.Grey.Darken1);
                    return;
                }

                box.Item().Element(c => Paragraph(c, "What was found out", data.Findings));
                box.Item().PaddingTop(6).Element(c => Paragraph(c, "What is being done so it does not happen again", data.CorrectiveAction));


                if (data.RecoveredOn is { } found)
                {
                    box.Item().PaddingTop(6).Text(t =>
                    {
                        t.Span("Found and recovered on: ").FontColor(Colors.Grey.Darken1);
                        t.Span(Date(found)).Bold();
                    });
                }
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
                SignatureLine(row.RelativeItem(), "Approved by (biomedical engineer)", data.ApprovedBy,
                    data.ApprovedAtUtc is { } approved ? ReportTime.DateTime(approved, utcOffset) : null);
            });
        });
    }

    /// <summary>
    /// The head of Biomedical's final decision, and the head's own signature. The name and the day are printed once the head has
    /// decided in the program; the line above them is for the signature itself, which is made by hand on the paper.
    /// </summary>
    private void DecisionBlock(IContainer container)
    {
        container.Column(col =>
        {
            col.Item().Text("Final decision of the Head of Biomedical").FontSize(11).Bold();

            col.Item().PaddingTop(3).Border(1).BorderColor(Colors.Grey.Darken1).Padding(10).Column(box =>
            {
                box.Item().Text(t =>
                {
                    t.Span("Decision: ").FontColor(Colors.Grey.Darken1);
                    t.Span(data.StatusLabel).Bold();
                });

                box.Item().PaddingTop(6).Element(c => Paragraph(c, "Remarks of the head", data.DecisionNotes));

                box.Item().PaddingTop(10).Row(row =>
                {
                    row.RelativeItem(2).Column(sig =>
                    {
                        // Tall enough to sign in, and to stamp beside.
                        sig.Item().Height(16, Unit.Millimetre);
                        sig.Item().LineHorizontal(0.7f).LineColor(Colors.Grey.Darken2);
                        sig.Item().PaddingTop(2).Text("Signature of the Head of Biomedical").FontSize(8).FontColor(Colors.Grey.Darken1);
                    });

                    row.ConstantItem(8, Unit.Millimetre);

                    row.RelativeItem(2).Column(who =>
                    {
                        who.Item().Height(16, Unit.Millimetre).AlignBottom().Text(string.IsNullOrWhiteSpace(data.ClosedBy) ? string.Empty : data.ClosedBy).Bold();
                        who.Item().LineHorizontal(0.7f).LineColor(Colors.Grey.Darken2);
                        who.Item().PaddingTop(2).Text("Name").FontSize(8).FontColor(Colors.Grey.Darken1);
                    });

                    row.ConstantItem(8, Unit.Millimetre);

                    row.RelativeItem(1.3f).Column(day =>
                    {
                        day.Item().Height(16, Unit.Millimetre).AlignBottom()
                            .Text(data.ClosedAtUtc is { } closed ? ReportTime.DateTime(closed, utcOffset) : string.Empty).FontSize(9);
                        day.Item().LineHorizontal(0.7f).LineColor(Colors.Grey.Darken2);
                        day.Item().PaddingTop(2).Text("Date").FontSize(8).FontColor(Colors.Grey.Darken1);
                    });
                });

                box.Item().PaddingTop(8).Text("Seal / stamp").FontSize(8).FontColor(Colors.Grey.Darken1);
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
                    .Text($"Generated by Hospital PM from the loss record. About the machine only: no patient information is held here. Times are {ReportTime.Zone(utcOffset)}.")
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
            row.ConstantItem(34, Unit.Millimetre).Text(label).FontSize(8.5f).FontColor(Colors.Grey.Darken1);

            var text = row.RelativeItem().Text(string.IsNullOrWhiteSpace(value) ? "—" : value);
            if (bold)
            {
                text.Bold();
            }
        });
    }
}
