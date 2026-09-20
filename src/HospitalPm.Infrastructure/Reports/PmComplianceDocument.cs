using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace HospitalPm.Infrastructure.Reports;

/// <summary>
/// The report a biomedical head hands an accreditation assessor: for a period,
/// how many PMs fell due, how many were done on time, and every one that was not.
///
/// It leads with the figure and then explains it. The exceptions are listed in
/// full rather than summarised, because an auditor will ask for the machine, not
/// the percentage. Nothing here is typed in by hand: it is computed from the
/// register, and the rules it used are printed at the end.
/// </summary>
public sealed class PmComplianceDocument(ComplianceReport report, ReportOptions options) : IDocument
{
    // Bundled with QuestPDF, so it renders the same on a hospital PC and on a
    // container with no system fonts. See PmCertificateDocument.
    private const string BundledFont = "Lato";

    private static readonly string Accent = Colors.Blue.Darken2;

    public void Compose(IDocumentContainer container)
    {
        container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(16, Unit.Millimetre);
            page.DefaultTextStyle(t => t.FontFamily(BundledFont).FontSize(9));

            page.Header().Element(Header);
            page.Content().Element(Content);
            page.Footer().Element(Footer);
        });
    }

    private static string Date(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Pct(double? p) => p is null ? "—" : $"{p.Value.ToString("0.0", CultureInfo.InvariantCulture)}%";

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

                    if (!string.IsNullOrWhiteSpace(options.HospitalSubtitle))
                    {
                        left.Item().Text(options.HospitalSubtitle!).FontSize(9).FontColor(Colors.Grey.Darken1);
                    }

                    left.Item().PaddingTop(2).Text("Biomedical Engineering Department")
                        .FontSize(9).FontColor(Colors.Grey.Darken1);
                });

                row.ConstantItem(62, Unit.Millimetre).AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text("PREVENTIVE MAINTENANCE").FontSize(10).Bold().FontColor(Accent);
                    right.Item().AlignRight().Text("COMPLIANCE REPORT").FontSize(10).Bold().FontColor(Accent);
                    right.Item().PaddingTop(3).AlignRight().Text($"{Date(report.From)} to {Date(report.To)}").FontSize(9);
                });
            });

            col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Accent);
        });
    }

    private void Content(IContainer container)
    {
        container.PaddingVertical(8).Column(col =>
        {
            col.Spacing(11);

            col.Item().Element(Summary);
            col.Item().Element(Breakdown("By department", report.ByDepartment));
            col.Item().Element(Breakdown("By equipment type", report.ByType));
            col.Item().Element(ExceptionsBlock);
            col.Item().Element(FindingsBlock);

            // Kept together: a sign-off line alone on a last page, with nothing
            // above it to say what is being signed, is not a sign-off.
            col.Item().ShowEntire().Column(end =>
            {
                end.Spacing(11);
                end.Item().Element(Method);
                end.Item().Element(SignOff);
            });
        });
    }

    private void Summary(IContainer container)
    {
        var t = report.Totals;
        var tone = t.Due == 0 ? Colors.Grey.Lighten3
            : t.OnSchedulePercent >= 95 ? Colors.Green.Lighten4
            : t.OnSchedulePercent >= 80 ? Colors.Orange.Lighten4
            : Colors.Red.Lighten4;

        container.Column(col =>
        {
            col.Item().Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    Field(left, "Period", $"{Date(report.From)} to {Date(report.To)}");
                    Field(left, "Scope", report.Scope);
                    Field(left, "Counted through", Date(report.CountedThrough)
                        + (report.CountedThrough < report.To ? " (the period has not ended)" : string.Empty));
                });

                row.RelativeItem().Column(right =>
                {
                    Field(right, "Prepared", $"{ReportTime.DateTime(report.GeneratedAtUtc, report.Offset)} {ReportTime.Zone(report.Offset)}");
                    Field(right, "Prepared by", report.GeneratedBy);
                    Field(right, "Machines covered", report.Machines.ToString(CultureInfo.InvariantCulture));
                });
            });

            col.Item().PaddingTop(8).Background(tone).Padding(8).Row(row =>
            {
                row.ConstantItem(38, Unit.Millimetre).AlignMiddle().Column(c =>
                {
                    c.Item().Text(Pct(t.OnSchedulePercent)).FontSize(22).Bold();
                    c.Item().Text("done on schedule").FontSize(8);
                });

                row.RelativeItem().AlignMiddle().Text(text =>
                {
                    text.Span(t.Due == 0
                        ? "No PM fell due in this period."
                        : $"{t.OnTime} of {t.Due} PM{(t.Due == 1 ? string.Empty : "s")} that fell due were done by their due date. ");

                    if (t.Due > 0)
                    {
                        text.Span($"{t.Completed} ({Pct(t.CompletionPercent)}) were done at all.");
                    }
                });
            });

            col.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    for (var i = 0; i < 7; i++) c.RelativeColumn();
                });

                Stat(table, "Fell due", t.Due);
                Stat(table, "Done on time", t.OnTime);
                Stat(table, "Done late", t.Late);
                Stat(table, "Skipped", t.Skipped);
                Stat(table, "Overdue, not done", t.Overdue);
                Stat(table, "Due, in grace", t.WithinGrace);
                Stat(table, "With findings", t.WithFindings);
            });

            if (report.NotYetDue > 0)
            {
                col.Item().PaddingTop(4).Text(
                        $"{report.NotYetDue} more PM{(report.NotYetDue == 1 ? string.Empty : "s")} in this period "
                        + "have not fallen due yet and are not counted.")
                    .FontSize(8).FontColor(Colors.Grey.Darken1);
            }
        });
    }

    private static void Stat(TableDescriptor table, string label, int value)
    {
        table.Cell().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(4).Column(c =>
        {
            c.Item().Text(value.ToString(CultureInfo.InvariantCulture)).FontSize(13).Bold();
            c.Item().Text(label).FontSize(7.5f).FontColor(Colors.Grey.Darken1);
        });
    }

    private Action<IContainer> Breakdown(string title, IReadOnlyList<ComplianceGroup> groups) => container =>
    {
        if (groups.Count == 0)
        {
            return;
        }

        container.Column(col =>
        {
            col.Item().PaddingBottom(3).Text(title).FontSize(11).Bold();

            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(4);
                    for (var i = 0; i < 6; i++) c.RelativeColumn(1);
                });

                table.Header(h =>
                {
                    HeaderCell(h.Cell(), char.ToUpperInvariant(title[3]) + title[4..]);
                    foreach (var name in new[] { "Due", "On time", "Late", "Skipped", "Overdue", "On sched." })
                    {
                        HeaderCell(h.Cell().AlignRight(), name);
                    }
                });

                foreach (var g in groups)
                {
                    var x = g.Totals;
                    var bg = x.Overdue + x.Skipped > 0 ? Colors.Orange.Lighten5 : Colors.White;

                    table.Cell().Background(bg).Element(Cell).Text(g.Name);
                    table.Cell().Background(bg).Element(Cell).AlignRight().Text(x.Due.ToString(CultureInfo.InvariantCulture));
                    table.Cell().Background(bg).Element(Cell).AlignRight().Text(x.OnTime.ToString(CultureInfo.InvariantCulture));
                    table.Cell().Background(bg).Element(Cell).AlignRight().Text(x.Late.ToString(CultureInfo.InvariantCulture));
                    table.Cell().Background(bg).Element(Cell).AlignRight().Text(x.Skipped.ToString(CultureInfo.InvariantCulture));
                    table.Cell().Background(bg).Element(Cell).AlignRight().Text(x.Overdue.ToString(CultureInfo.InvariantCulture));
                    table.Cell().Background(bg).Element(Cell).AlignRight().Text(Pct(x.OnSchedulePercent)).Bold();
                }
            });
        });
    };

    private void ExceptionsBlock(IContainer container)
    {
        var lines = report.Exceptions.ToList();

        // A heading is not left at the foot of a page with its table on the next.
        container.EnsureSpace(90).Column(col =>
        {
            col.Item().PaddingBottom(3).Text("PMs that were late, overdue or skipped").FontSize(11).Bold();

            if (lines.Count == 0)
            {
                col.Item().Text("None. Every PM that fell due in this period was done on time.")
                    .FontColor(Colors.Green.Darken3);
                return;
            }

            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(2.2f);  // asset
                    c.RelativeColumn(3);     // type + place
                    c.RelativeColumn(1.5f);  // due
                    c.RelativeColumn(4);     // what happened
                });

                table.Header(h =>
                {
                    HeaderCell(h.Cell(), "Asset");
                    HeaderCell(h.Cell(), "Equipment and place");
                    HeaderCell(h.Cell(), "Due");
                    HeaderCell(h.Cell(), "What happened");
                });

                foreach (var l in lines)
                {
                    var bg = l.Outcome == PmOutcome.Overdue ? Colors.Red.Lighten5
                        : l.Outcome == PmOutcome.Skipped ? Colors.Orange.Lighten5 : Colors.White;

                    table.Cell().Background(bg).Element(Cell).Text(l.Task.AssetTag).Bold();
                    table.Cell().Background(bg).Element(Cell).Column(c =>
                    {
                        c.Item().Text(l.Task.EquipmentType);
                        c.Item().Text(l.Task.Location).FontSize(8).FontColor(Colors.Grey.Darken1);
                    });
                    table.Cell().Background(bg).Element(Cell).Text(Date(l.Task.DueDate));
                    table.Cell().Background(bg).Element(Cell).Text(What(l));
                }
            });
        });
    }

    private static string What(ComplianceLine l) => l.Outcome switch
    {
        PmOutcome.Late =>
            $"Done {Date(l.PerformedOn!.Value)}, {l.DaysLate} day{(l.DaysLate == 1 ? string.Empty : "s")} after it fell due"
            + (string.IsNullOrWhiteSpace(l.Task.PerformedBy) ? "." : $", by {l.Task.PerformedBy}."),
        PmOutcome.Skipped =>
            $"Skipped. Reason: {(string.IsNullOrWhiteSpace(l.Task.SkipReason) ? "not recorded" : l.Task.SkipReason)}",
        _ => $"Not done, {l.DaysLate} day{(l.DaysLate == 1 ? string.Empty : "s")} past its due date.",
    };

    private void FindingsBlock(IContainer container)
    {
        var lines = report.Findings.ToList();

        container.EnsureSpace(90).Column(col =>
        {
            col.Item().PaddingBottom(3).Text("Completed PMs that recorded a finding").FontSize(11).Bold();
            col.Item().PaddingBottom(3).Text(
                    "A reading outside its specification, or a check answered Fail. Recorded honestly at the time; "
                    + "each has its own certificate.")
                .FontSize(8).FontColor(Colors.Grey.Darken1);

            if (lines.Count == 0)
            {
                col.Item().Text("None recorded in this period.").FontColor(Colors.Green.Darken3);
                return;
            }

            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(2.2f);
                    c.RelativeColumn(3);
                    c.RelativeColumn(1.5f);
                    c.RelativeColumn(1.5f);
                    c.RelativeColumn(1.4f);
                });

                table.Header(h =>
                {
                    HeaderCell(h.Cell(), "Asset");
                    HeaderCell(h.Cell(), "Equipment");
                    HeaderCell(h.Cell(), "Due");
                    HeaderCell(h.Cell(), "Out of spec");
                    HeaderCell(h.Cell(), "Failed");
                });

                foreach (var l in lines)
                {
                    table.Cell().Element(Cell).Text(l.Task.AssetTag).Bold();
                    table.Cell().Element(Cell).Text(l.Task.EquipmentType);
                    table.Cell().Element(Cell).Text(Date(l.Task.DueDate));
                    table.Cell().Element(Cell).Text(l.Task.OutOfRangeReadings.ToString(CultureInfo.InvariantCulture));
                    table.Cell().Element(Cell).Text(l.Task.FailedChecks.ToString(CultureInfo.InvariantCulture));
                }
            });
        });
    }

    private void Method(IContainer container)
    {
        container.Border(1).BorderColor(Colors.Grey.Lighten2).Padding(8).Column(col =>
        {
            col.Spacing(2);
            col.Item().Text("How these figures are worked out").FontSize(9.5f).Bold();

            foreach (var line in new[]
                     {
                         "Counted: every scheduled PM whose due date falls in the period and has arrived. A PM not yet due is not counted.",
                         "Done on time: performed on or before its due date plus the grace days set on its schedule. Done late: performed after that.",
                         "The date a PM was performed is the technician's own time on the device, on the hospital's calendar, not the time it reached the server.",
                         "Skipped is not done. It counts against the figure, and the recorded reason is listed above.",
                         "Done on schedule = done on time / fell due. Done at all = (on time + late) / fell due.",
                         "Computed from the equipment register and the recorded PMs. Nothing on this report is entered by hand. "
                         + "Each completed PM keeps its own signed certificate, and the checklist version it was filled under.",
                     })
            {
                col.Item().Text("•  " + line).FontSize(8).FontColor(Colors.Grey.Darken2);
            }
        });
    }

    private static void SignOff(IContainer container)
    {
        container.PaddingTop(6).Row(row =>
        {
            row.RelativeItem().Element(SignLine("Prepared by"));
            row.ConstantItem(12);
            row.RelativeItem().Element(SignLine("Reviewed by"));
            row.ConstantItem(12);
            row.ConstantItem(34, Unit.Millimetre).Element(SignLine("Date"));
        });
    }

    private static Action<IContainer> SignLine(string label) => c =>
        c.Column(col =>
        {
            col.Item().Height(16, Unit.Millimetre).BorderBottom(1).BorderColor(Colors.Grey.Medium);
            col.Item().PaddingTop(2).Text(label).FontSize(8).FontColor(Colors.Grey.Darken1);
        });

    private void Footer(IContainer container)
    {
        container.Row(row =>
        {
            row.RelativeItem().Text(text =>
            {
                text.DefaultTextStyle(s => s.FontSize(7.5f).FontColor(Colors.Grey.Darken1));

                text.Span($"{(string.IsNullOrWhiteSpace(options.HospitalName) ? "Hospital PM" : options.HospitalName)} · PM compliance {Date(report.From)} to {Date(report.To)}");

                if (!string.IsNullOrWhiteSpace(options.AccreditationReference))
                {
                    text.Span($" · {options.AccreditationReference}");
                }
            });

            row.ConstantItem(50).AlignRight().Text(text =>
            {
                text.DefaultTextStyle(s => s.FontSize(7.5f).FontColor(Colors.Grey.Darken1));
                text.Span("Page ");
                text.CurrentPageNumber();
                text.Span(" of ");
                text.TotalPages();
            });
        });
    }

    private static void Field(ColumnDescriptor col, string label, string? value)
    {
        col.Item().PaddingBottom(2).Row(r =>
        {
            r.ConstantItem(30, Unit.Millimetre).Text(label).FontSize(8).FontColor(Colors.Grey.Darken1);
            r.RelativeItem().Text(string.IsNullOrWhiteSpace(value) ? "—" : value);
        });
    }

    private static void HeaderCell(IContainer container, string text) =>
        container.Background(Colors.Grey.Lighten3).PaddingVertical(3).PaddingHorizontal(4)
            .Text(text).FontSize(8.5f).Bold();

    private static IContainer Cell(IContainer container) =>
        container.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3).PaddingHorizontal(4);
}
