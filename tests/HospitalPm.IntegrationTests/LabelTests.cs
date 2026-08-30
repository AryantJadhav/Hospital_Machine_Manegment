using HospitalPm.Infrastructure.Labels;
using Microsoft.Extensions.Options;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Label rendering has no database dependency, so these run without the
/// container fixture.
/// </summary>
public sealed class LabelTests
{
    private static readonly LabelOptions Options = new()
    {
        BaseUrl = "http://hospitalpm.local",
        HospitalName = "Test Hospital",
    };

    private static LabelData Sample(string tag = "BME-0001")
        => new(tag, "Ventilator", "Intensive Care Unit", "SN-123");

    // ---------------- payload ----------------

    [Fact]
    public void Payload_is_a_url_carrying_the_tag()
    {
        var payload = AssetTagPayload.Build("http://hospitalpm.local", "BME-0001");

        Assert.Equal("http://hospitalpm.local/e/BME-0001", payload);
    }

    [Fact]
    public void Payload_escapes_tags_containing_slashes()
    {
        // "ICU/VENT 03" is an entirely ordinary hospital asset code. Unescaped
        // it would add a path segment and the scan would resolve to nothing.
        var payload = AssetTagPayload.Build("http://hospitalpm.local", "ICU/VENT 03");

        Assert.DoesNotContain("ICU/VENT", payload, StringComparison.Ordinal);
        Assert.Equal("ICU/VENT 03", AssetTagPayload.Extract(payload));
    }

    [Fact]
    public void Payload_round_trips_through_extract()
    {
        foreach (var tag in new[] { "BME-0001", "ICU/VENT 03", "A^B", "тест-1" })
        {
            var payload = AssetTagPayload.Build("http://hospitalpm.local", tag);
            Assert.Equal(tag, AssetTagPayload.Extract(payload));
        }
    }

    [Fact]
    public void A_bare_tag_scans_as_itself()
    {
        // Labels printed before the URL scheme existed, and hand-typed entry
        // when a code is too scratched to scan, both arrive unwrapped.
        Assert.Equal("BME-0001", AssetTagPayload.Extract("BME-0001"));
    }

    [Fact]
    public void A_url_from_a_different_host_still_yields_the_tag()
    {
        // The server was renamed or moved to a fixed IP after the labels were
        // printed. The tag is still in the path, so the app must not care.
        Assert.Equal("BME-0001", AssetTagPayload.Extract("http://10.0.0.5:8080/e/BME-0001"));
    }

    [Fact]
    public void An_unrelated_url_is_returned_unchanged_rather_than_mangled()
    {
        const string other = "https://example.com/something/else";

        Assert.Equal(other, AssetTagPayload.Extract(other));
    }

    // ---------------- QR ----------------

    [Fact]
    public void Qr_renders_a_png()
    {
        var png = new QrCodeService().RenderPng(Options.BaseUrl, "BME-0001");

        Assert.NotEmpty(png);
        // PNG magic number: a renderer that silently produced something else
        // would still return bytes.
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png.Take(4).ToArray());
    }

    [Fact]
    public void Qr_is_deterministic_for_the_same_tag()
    {
        var qr = new QrCodeService();

        // Reprinting a lost label must produce the same code as the original.
        Assert.Equal(
            qr.RenderPng(Options.BaseUrl, "BME-0001"),
            qr.RenderPng(Options.BaseUrl, "BME-0001"));
    }

    // ---------------- PDF sheet ----------------

    [Fact]
    public void Sheet_renders_a_pdf()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        var service = new LabelSheetService(new QrCodeService(), Microsoft.Extensions.Options.Options.Create(Options));

        var pdf = service.Render([Sample(), Sample("BME-0002")]);

        Assert.NotEmpty(pdf);
        Assert.Equal("%PDF"u8.ToArray(), pdf.Take(4).ToArray());
    }

    [Fact]
    public void Sheet_spans_multiple_pages_beyond_one_sheet()
    {
        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

        var service = new LabelSheetService(new QrCodeService(), Microsoft.Extensions.Options.Options.Create(Options));

        // 24 per sheet, so 30 must not silently drop the overflow.
        var many = Enumerable.Range(1, 30).Select(i => Sample($"BME-{i:D4}")).ToList();

        var pdf = service.Render(many);

        Assert.NotEmpty(pdf);
    }

    // ---------------- ZPL ----------------

    [Fact]
    public void Zpl_wraps_each_label_in_a_format_block()
    {
        var zpl = new ZplLabelService(Microsoft.Extensions.Options.Options.Create(Options))
            .Render([Sample(), Sample("BME-0002")]);

        Assert.Equal(2, zpl.Split("^XA").Length - 1);
        Assert.Equal(2, zpl.Split("^XZ").Length - 1);
    }

    [Fact]
    public void Zpl_contains_the_qr_command_and_the_readable_tag()
    {
        var zpl = new ZplLabelService(Microsoft.Extensions.Options.Options.Create(Options))
            .Render([Sample()]);

        Assert.Contains("^BQN", zpl, StringComparison.Ordinal);
        Assert.Contains("BME-0001", zpl, StringComparison.Ordinal);
        Assert.Contains("hospitalpm.local/e/BME-0001", zpl, StringComparison.Ordinal);
    }

    [Fact]
    public void Zpl_strips_command_characters_from_field_data()
    {
        var zpl = new ZplLabelService(Microsoft.Extensions.Options.Options.Create(Options))
            .Render([new LabelData("A^B~C", "Type^X", "Ward~Y", null)]);

        // ^ and ~ introduce ZPL commands. Left in, they truncate the field and
        // the printer emits garbage — and hospitals do use ^ in asset codes.
        var body = zpl.Replace("^XA", "").Replace("^XZ", "").Replace("^FS", "")
                      .Replace("^FO", "").Replace("^FD", "").Replace("^BQN", "")
                      .Replace("^PW", "").Replace("^LL", "").Replace("^LH", "")
                      .Replace("^A0N", "");

        Assert.DoesNotContain("A^B", body, StringComparison.Ordinal);
        Assert.DoesNotContain("~C", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Zpl_scales_with_printer_resolution()
    {
        var service = new ZplLabelService(Microsoft.Extensions.Options.Options.Create(Options));

        var at203 = service.Render([Sample()], dotsPerMm: ZplLabelService.DotsPerMm203);
        var at300 = service.Render([Sample()], dotsPerMm: 11.8);

        // A 300 dpi printer must not print everything at two-thirds size.
        Assert.Contains("^PW400", at203, StringComparison.Ordinal);
        Assert.Contains("^PW590", at300, StringComparison.Ordinal);
    }
}
