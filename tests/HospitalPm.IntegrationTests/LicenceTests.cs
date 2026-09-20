using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HospitalPm.Domain.Licensing;
using HospitalPm.Infrastructure.Licensing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Offline licence verification.
///
/// No database and no container: this is pure crypto and file handling, and
/// the interesting cases are the dishonest ones. A licence check that only
/// proves a genuine licence works has tested nothing worth testing.
/// </summary>
public sealed class LicenceTests : IDisposable
{
    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly List<string> _paths = [];

    private string PublicKey => Convert.ToBase64String(_signer.ExportSubjectPublicKeyInfo());

    public void Dispose()
    {
        _signer.Dispose();

        foreach (var path in _paths)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A stray temp file is not worth failing a run.
            }
        }
    }

    private string TempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hospitalpm-{Guid.NewGuid():N}.licence");
        _paths.Add(path);
        return path;
    }

    private string Sign(Licence licence, ECDsa? key = null)
    {
        var payload = LicenceFile.Serialise(licence);
        var signature = (key ?? _signer).SignData(
            payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return LicenceFile.Format(payload, signature);
    }

    private static Licence Sample(DateOnly? expires = null, params string[] modules) => new(
        LicenceId: Guid.NewGuid(),
        HospitalName: "Sahyadri Hospital, Pune",
        IssuedOn: new DateOnly(2026, 1, 1),
        ExpiresOn: expires,
        Modules: modules,
        MaxEquipment: 2000,
        Notes: "PO 2026/114");

    private static readonly DateOnly Today = new(2026, 6, 1);

    [Fact]
    public void A_genuine_licence_verifies_and_keeps_its_details()
    {
        var status = new LicenceVerifier(PublicKey)
            .Verify(Sign(Sample(new DateOnly(2027, 3, 31), "escalation")), Today);

        Assert.Equal(LicenceState.Valid, status.State);
        Assert.True(status.IsLicensed);
        Assert.Equal("Sahyadri Hospital, Pune", status.Licence!.HospitalName);
        Assert.Equal(2000, status.Licence.MaxEquipment);
        Assert.True(status.HasModule("escalation"));
        Assert.True(status.HasModule("ESCALATION"));
        Assert.False(status.HasModule("spare-parts"));
    }

    [Fact]
    public void A_perpetual_licence_never_expires()
    {
        var status = new LicenceVerifier(PublicKey)
            .Verify(Sign(Sample(expires: null)), new DateOnly(2099, 12, 31));

        Assert.Equal(LicenceState.Valid, status.State);
        Assert.Contains("perpetual", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Editing_the_payload_to_extend_the_expiry_is_refused()
    {
        var genuine = Sign(Sample(new DateOnly(2026, 3, 31)));
        var parsed = LicenceFile.Parse(genuine)!;

        // The obvious attack: decode the payload, push the date out, re-encode,
        // keep the original signature.
        var json = JsonSerializer.Deserialize<JsonElement>(parsed.Payload);
        var edited = json.GetRawText().Replace("2026-03-31", "2099-12-31", StringComparison.Ordinal);
        var tampered = LicenceFile.Format(Encoding.UTF8.GetBytes(edited), parsed.Signature);

        var status = new LicenceVerifier(PublicKey).Verify(tampered, Today);

        Assert.Equal(LicenceState.Invalid, status.State);
        Assert.False(status.IsLicensed);
        Assert.Null(status.Licence);
    }

    [Fact]
    public void A_licence_signed_by_a_different_key_is_refused()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var status = new LicenceVerifier(PublicKey)
            .Verify(Sign(Sample(new DateOnly(2099, 1, 1)), attacker), Today);

        Assert.Equal(LicenceState.Invalid, status.State);

        // The message does not distinguish forged from edited. Both mean the
        // same thing to the reader, and detail only helps whoever is trying.
        Assert.DoesNotContain("signature", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not a licence")]
    [InlineData("-----BEGIN HOSPITALPM LICENCE-----\nnot base64!!\n-----SIGNATURE-----\nx\n-----END HOSPITALPM LICENCE-----")]
    public void Damaged_files_are_invalid_rather_than_crashing(string text)
    {
        var status = new LicenceVerifier(PublicKey).Verify(text, Today);

        // Licences arrive as email attachments and get mangled. None of this
        // may throw.
        Assert.NotEqual(LicenceState.Valid, status.State);
        Assert.False(string.IsNullOrWhiteSpace(status.Message));
    }

    [Fact]
    public void An_expired_licence_says_so_but_still_names_the_hospital()
    {
        var status = new LicenceVerifier(PublicKey)
            .Verify(Sign(Sample(new DateOnly(2026, 5, 25))), Today);

        Assert.Equal(LicenceState.Expired, status.State);
        Assert.False(status.IsLicensed);

        // Kept, because the hospital name and support id stay useful after
        // expiry - and because nothing has stopped working yet.
        Assert.NotNull(status.Licence);
        Assert.Contains("7 days ago", status.Message, StringComparison.Ordinal);
        Assert.Contains("still works", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expiry_is_inclusive_of_the_last_day()
    {
        var expiry = new DateOnly(2026, 6, 1);
        var verifier = new LicenceVerifier(PublicKey);

        // A licence "valid until 01/06" must work all of that day. Ending it at
        // midnight would cut a hospital off a day early.
        Assert.Equal(LicenceState.Valid, verifier.Verify(Sign(Sample(expiry)), expiry).State);
        Assert.Equal(LicenceState.Expired,
            verifier.Verify(Sign(Sample(expiry)), expiry.AddDays(1)).State);
    }

    [Fact]
    public void No_licence_file_runs_unlicensed_rather_than_failing()
    {
        var service = new LicenceService(
            Options.Create(new LicenceOptions { PublicKey = PublicKey, Path = TempPath() }),
            TimeProvider.System);

        var status = service.Current();

        // A pilot install has to work before anyone has issued a licence.
        Assert.Equal(LicenceState.Missing, status.State);
        Assert.False(status.IsLicensed);
    }

    [Fact]
    public void Installing_a_good_licence_writes_it_and_it_reads_back()
    {
        var path = TempPath();
        var service = new LicenceService(
            Options.Create(new LicenceOptions { PublicKey = PublicKey, Path = path }),
            TimeProvider.System);

        var (saved, status) = service.Install(Sign(Sample(new DateOnly(2099, 1, 1))));

        Assert.True(saved);
        Assert.Equal(LicenceState.Valid, status.State);
        Assert.True(File.Exists(path));
        Assert.Equal(LicenceState.Valid, service.Current().State);
    }

    [Fact]
    public void A_bad_licence_never_overwrites_a_good_one()
    {
        var path = TempPath();
        var service = new LicenceService(
            Options.Create(new LicenceOptions { PublicKey = PublicKey, Path = path }),
            TimeProvider.System);

        service.Install(Sign(Sample(new DateOnly(2099, 1, 1))));
        var good = File.ReadAllText(path);

        var (saved, status) = service.Install("-----BEGIN HOSPITALPM LICENCE-----\njunk\n-----SIGNATURE-----\njunk\n-----END HOSPITALPM LICENCE-----");

        Assert.False(saved);
        Assert.Equal(LicenceState.Invalid, status.State);

        // The file on disk is untouched, so a truncated email attachment
        // cannot cost a hospital the licence it already had.
        Assert.Equal(good, File.ReadAllText(path));
        Assert.Equal(LicenceState.Valid, service.Current().State);
    }

    [Fact]
    public void A_build_with_no_public_key_reports_that_rather_than_calling_everything_forged()
    {
        var service = new LicenceService(
            Options.Create(new LicenceOptions { PublicKey = "", Path = TempPath() }),
            TimeProvider.System);

        var status = service.Current();

        Assert.Equal(LicenceState.Missing, status.State);
        Assert.Contains("no licence key configured", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_wrapper_survives_being_pasted_into_an_email()
    {
        var original = Sign(Sample(new DateOnly(2099, 1, 1)));

        // Windows line endings, indentation and a trailing blank line are what
        // a copy-paste out of an email client actually produces.
        var mangled = original.Replace("\n", "\r\n", StringComparison.Ordinal)
            .Replace("\r\n", "\r\n   ", StringComparison.Ordinal) + "\r\n\r\n";

        Assert.Equal(LicenceState.Valid,
            new LicenceVerifier(PublicKey).Verify(mangled, Today).State);
    }

    // --- The release path ---------------------------------------------------
    //
    // installer/build.ps1 writes the public key into the PUBLISHED
    // appsettings.json at release time, and refuses to build without one.
    // Neither of the two things that has to hold for that to work is
    // exercised by the tests above, and both fail silently: a build with the
    // wrong key shape rejects every licence, and a build whose key never
    // reached LicenceOptions reports "licensing is not enforced" while working
    // perfectly.

    /// <summary>
    /// The settings build.ps1 fills in have to still be there, still be empty,
    /// and still be findable one section at a time.
    ///
    /// There are two public keys now, the licence one and the update one, and
    /// build.ps1 writes each with a regex scoped to its own section, asserting
    /// a single match. Renaming a setting, pre-filling one, or moving
    /// PublicKey out of first position in its object breaks a release at the
    /// moment a release is being cut, which is the worst time to find out.
    /// This fails on the pull request instead.
    ///
    /// It also stops a real public key being committed. Neither key is secret,
    /// but a key in source is one nobody chose per release.
    /// </summary>
    [Theory]
    [InlineData("Licence")]
    [InlineData("Update")]
    public void The_shipped_settings_leave_an_empty_public_key_for_the_release_build(string section)
    {
        var settingsPath = FindRepositoryFile("src/HospitalPm.Api/appsettings.json");
        var text = File.ReadAllText(settingsPath);

        // The same section-scoped pattern installer/build.ps1 uses. A pattern
        // matching either section would let a release write the update key
        // into the licence slot — a build that then rejects every licence ever
        // issued, and says so only on a page nobody opens for months.
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text, "(\"" + section + "\"\\s*:\\s*\\{\\s*\"PublicKey\"\\s*:\\s*)\"\"");

        Assert.Single(matches);

        using var document = JsonDocument.Parse(text);
        var publicKey = document.RootElement
            .GetProperty(section).GetProperty(nameof(LicenceOptions.PublicKey)).GetString();

        Assert.Equal(string.Empty, publicKey);
    }

    /// <summary>
    /// The same guard for the update feed URL, which build.ps1 fills in the
    /// same way and at the same moment.
    ///
    /// Here rather than with the update tests because this is about the shape
    /// of the one file the release path rewrites, and splitting that across
    /// two test classes is how half of it gets forgotten.
    ///
    /// A URL committed here would be one nobody chose per release, and every
    /// developer build would quietly point at the real feed.
    /// </summary>
    [Fact]
    public void The_shipped_settings_leave_an_empty_feed_url_for_the_release_build()
    {
        var settingsPath = FindRepositoryFile("src/HospitalPm.Api/appsettings.json");
        var text = File.ReadAllText(settingsPath);

        // Not section-scoped, unlike PublicKey: FeedUrl appears once in the
        // whole file, which is what build.ps1 relies on.
        var matches = System.Text.RegularExpressions.Regex.Matches(
            text, "(\"FeedUrl\"\\s*:\\s*)\"\"");

        Assert.Single(matches);

        using var document = JsonDocument.Parse(text);
        var feedUrl = document.RootElement.GetProperty("Update").GetProperty("FeedUrl").GetString();

        Assert.Equal(string.Empty, feedUrl);
    }

    /// <summary>
    /// A key sitting in appsettings.json under "Licence" actually reaches the
    /// service that verifies licences.
    ///
    /// This is the link the release path depends on and the one nothing else
    /// covers: every other test hands LicenceOptions to the service directly,
    /// so all of them would still pass if the configuration section were
    /// renamed and the shipped key bound to nothing.
    /// </summary>
    [Fact]
    public void A_public_key_in_configuration_reaches_the_service_that_verifies_licences()
    {
        var licencePath = TempPath();
        File.WriteAllText(licencePath, Sign(Sample(new DateOnly(2099, 1, 1))));

        // Shaped exactly like the file build.ps1 writes into.
        var json = $$"""
            {
              "Licence": {
                "PublicKey": "{{PublicKey}}",
                "Path": "{{licencePath.Replace("\\", "\\\\")}}"
              }
            }
            """;

        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var options = new LicenceOptions();
        configuration.GetSection(LicenceOptions.Section).Bind(options);

        var status = new LicenceService(Options.Create(options), TimeProvider.System).Current();

        Assert.Equal(LicenceState.Valid, status.State);
    }

    /// <summary>
    /// Walks up from the test assembly to the repository root.
    ///
    /// The test binary lives several directories below it and the depth
    /// depends on the configuration and target framework, so the root is found
    /// by looking for the file rather than counting "..".
    /// </summary>
    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not find {relativePath} above {AppContext.BaseDirectory}.");
    }
}
