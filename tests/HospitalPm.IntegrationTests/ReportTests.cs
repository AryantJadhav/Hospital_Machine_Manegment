using System.Text;
using System.Text.RegularExpressions;
using HospitalPm.Infrastructure.Reports;
using QuestPDF.Fluent;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Document rendering has no database dependency.
/// </summary>
public sealed class ReportTests
{
    private static readonly ReportOptions Options = new()
    {
        HospitalName = "Sunrise Multispecialty Hospital",
        HospitalSubtitle = "Pune, Maharashtra",
        AccreditationReference = "NABH ref. HOSP-2026-4471",
    };

    static ReportTests() =>
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

    private static PmCertificateData Certificate(bool clean = true) => new(
        "BME-0001",
        "Ventilator",
        "Intensive Care Unit",
        "SN-88213",
        "Philips",
        "V60",
        "Ventilator quarterly PM",
        2,
        new DateTime(2026, 8, 30, 9, 15, 0, DateTimeKind.Utc),
        new DateTime(2026, 8, 30, 20, 23, 0, DateTimeKind.Utc),
        new DateOnly(2026, 9, 1),
        "R. Kulkarni",
        "R. Kulkarni",
        null,
        null,
        "Filter replaced",
        [
            new CertificateLine("Visual inspection", "Casing intact and undamaged", "Pass", null, false, null),
            new CertificateLine(
                "Visual inspection", "Measure flow at 60 L/min",
                clean ? "60" : "52",
                clean ? null : "Below spec, escalated",
                !clean,
                "56–64 L/min"),
        ]);

    private static ServiceReportData ServiceReport() => new(
        "WO-2026-000001",
        "BME-0001",
        "Ventilator",
        "Intensive Care Unit",
        "SN-88213",
        "Philips",
        "V60",
        "Alarm sounding continuously, display blank",
        "Critical",
        "Closed",
        new DateTime(2026, 8, 28, 6, 0, 0, DateTimeKind.Utc),
        "Ward Sister",
        new DateTime(2026, 8, 28, 8, 30, 0, DateTimeKind.Utc),
        new DateTime(2026, 8, 30, 11, 0, 0, DateTimeKind.Utc),
        "R. Kulkarni",
        "Replaced flow sensor, recalibrated, verified against test lung",
        new DateTime(2026, 8, 28, 6, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 8, 30, 11, 0, 0, DateTimeKind.Utc),
        2940,
        [
            (new DateTime(2026, 8, 28, 8, 30, 0, DateTimeKind.Utc), "R. Kulkarni", "On site"),
            (new DateTime(2026, 8, 28, 9, 0, 0, DateTimeKind.Utc), "R. Kulkarni", "Flow sensor ordered"),
        ]);

    private static string Fonts(byte[] pdf)
        => string.Join(
            ",",
            Regex.Matches(Encoding.Latin1.GetString(pdf), @"/BaseFont\s*/([A-Za-z0-9+,\-]+)")
                .Select(m => m.Groups[1].Value));

    [Fact]
    public void A_certificate_renders()
    {
        var pdf = new PmCertificateDocument(Certificate(), Options).GeneratePdf();

        Assert.NotEmpty(pdf);
        Assert.Equal("%PDF"u8.ToArray(), pdf.Take(4).ToArray());
    }

    [Fact]
    public void A_service_report_renders()
    {
        var pdf = new ServiceReportDocument(ServiceReport(), Options).GeneratePdf();

        Assert.NotEmpty(pdf);
        Assert.Equal("%PDF"u8.ToArray(), pdf.Take(4).ToArray());
    }

    [Fact]
    public void Documents_embed_the_bundled_font_not_a_host_font()
    {
        var pdf = new PmCertificateDocument(Certificate(), Options).GeneratePdf();
        var fonts = Fonts(pdf);

        // The packaging constraint in document form. Naming Calibri or Arial
        // makes the output depend on fonts installed on the host, so a
        // minimal Linux container renders differently or not at all.
        Assert.Contains("Lato", fonts, StringComparison.Ordinal);
        Assert.DoesNotContain("Calibri", fonts, StringComparison.Ordinal);
        Assert.DoesNotContain("Arial", fonts, StringComparison.Ordinal);
    }

    [Fact]
    public void A_certificate_with_an_out_of_range_reading_is_not_reported_as_clean()
    {
        var withFinding = Certificate(clean: false);

        // A certificate that hides findings is worse than no certificate: it
        // makes the whole register untrustworthy the first time an auditor
        // checks one against the raw record.
        Assert.False(withFinding.IsClean);
        Assert.Equal(1, withFinding.OutOfRangeCount);

        Assert.True(Certificate().IsClean);
        Assert.Equal(0, Certificate().OutOfRangeCount);
    }

    [Fact]
    public void A_certificate_renders_with_a_malformed_signature()
    {
        var data = Certificate() with
        {
            Signature = [0x00, 0x01, 0x02, 0x03],
            SignatureFormat = "png",
        };

        // A corrupt signature must not stop a hospital printing the record.
        // The certificate still names who signed it.
        var pdf = new PmCertificateDocument(data, Options).GeneratePdf();

        Assert.NotEmpty(pdf);
    }

    [Fact]
    public void A_certificate_renders_an_svg_signature()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 320 180\">" +
                  "<path d=\"M10,90 L60,40 L110,120\" stroke=\"black\" stroke-width=\"2.5\" fill=\"none\"/></svg>";

        var data = Certificate() with
        {
            Signature = Encoding.UTF8.GetBytes(svg),
            SignatureFormat = "svg",
        };

        var pdf = new PmCertificateDocument(data, Options).GeneratePdf();

        Assert.NotEmpty(pdf);
    }

    [Fact]
    public void A_long_checklist_spills_onto_further_pages()
    {
        var many = Enumerable.Range(1, 120)
            .Select(i => new CertificateLine("Checks", $"Item {i}", "Pass", null, false, null))
            .ToList();

        var pdf = new PmCertificateDocument(Certificate() with { Lines = many }, Options).GeneratePdf();

        // A 120-item checklist must not silently truncate at the page break.
        Assert.True(pdf.Length > 20_000, $"expected a multi-page document, got {pdf.Length} bytes");
    }

    [Fact]
    public void A_report_renders_before_the_work_is_resolved()
    {
        var open = ServiceReport() with
        {
            Status = "InProgress",
            ResolvedAtUtc = null,
            ResolvedByName = null,
            ResolutionNotes = null,
            BackInServiceAtUtc = null,
            DowntimeMinutes = null,
        };

        // An engineer often needs the paperwork before the job is closed.
        var pdf = new ServiceReportDocument(open, Options).GeneratePdf();

        Assert.NotEmpty(pdf);
    }

    [Fact]
    public void A_report_renders_without_a_configured_hospital_name()
    {
        // A fresh install has not been through the setup wizard yet.
        var pdf = new ServiceReportDocument(ServiceReport(), new ReportOptions()).GeneratePdf();

        Assert.NotEmpty(pdf);
    }
}
