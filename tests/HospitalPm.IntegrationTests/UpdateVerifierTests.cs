using System.Security.Cryptography;
using HospitalPm.Domain.Updates;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The rules that decide whether an executable gets to run as LocalSystem on
/// a hospital's machine.
///
/// No database and no disk: the verifier takes its filesystem as an
/// interface, so every case here is a few lines of setup rather than
/// hundreds of megabytes of staging. That matters because the interesting
/// cases are the refusals, and there are a lot of them.
///
/// Each test signs its own manifest with a keypair made in the test. Nothing
/// is checked in, and a test that passes because it verified against the key
/// it just made would be circular — so the negative cases sign with a second
/// keypair, or edit the payload after signing.
/// </summary>
public sealed class UpdateVerifierTests
{
    private const string InstallerName = "HospitalPM-Setup-1.1.0.exe";
    private const long Size = 3_000_000;
    private static readonly string Hash = new('a', 64);

    private static readonly Version Running = new(1, 0, 0);

    private sealed record Keys(string PublicKey, ECDsa Private);

    private static Keys NewKeys()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new Keys(Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()), ecdsa);
    }

    private static UpdateManifest Manifest(
        string version = "1.1.0",
        string? installerFileName = null,
        string? sha256 = null,
        long size = Size) =>
        new(version, installerFileName ?? InstallerName, sha256 ?? Hash, size,
            new DateOnly(2026, 9, 8), "What changed.");

    private static string Sign(UpdateManifest manifest, ECDsa key)
    {
        var payload = UpdateManifestFile.Serialise(manifest);
        var signature = key.SignData(
            payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return UpdateManifestFile.Format(payload, signature);
    }

    /// <summary>A disk that answers whatever the test says, and counts hashing.</summary>
    private sealed class FakeFiles : IUpdateFileSystem
    {
        public bool Exists { get; init; } = true;
        public long Size { get; init; } = UpdateVerifierTests.Size;
        public string Hash { get; init; } = UpdateVerifierTests.Hash;
        public int HashCalls { get; private set; }

        public string Combine(string folder, string fileName) => $"{folder}/{fileName}";
        public bool FileExists(string path) => Exists;
        public long FileSize(string path) => Size;

        public string Sha256Hex(string path)
        {
            HashCalls++;
            return Hash;
        }
    }

    private static UpdateCandidate Verify(
        string? text, string publicKey, IUpdateFileSystem? files = null, Version? running = null) =>
        new UpdateVerifier(publicKey).Verify(
            "/updates/x.update", text, "/updates", running ?? Running, files ?? new FakeFiles());

    [Fact]
    public void A_properly_signed_update_with_a_matching_installer_is_ready()
    {
        using var keys = NewKeys().Private;
        var publicKey = Convert.ToBase64String(keys.ExportSubjectPublicKeyInfo());

        var result = Verify(Sign(Manifest(), keys), publicKey);

        Assert.Equal(UpdateState.Ready, result.State);
        Assert.True(result.CanInstall);
        Assert.Equal("1.1.0", result.Manifest?.Version);
        Assert.Equal("/updates/HospitalPM-Setup-1.1.0.exe", result.InstallerPath);
    }

    [Fact]
    public void An_update_signed_by_someone_else_is_refused()
    {
        var ours = NewKeys();
        var theirs = NewKeys();
        using var _ = ours.Private;
        using var __ = theirs.Private;

        var result = Verify(Sign(Manifest(), theirs.Private), ours.PublicKey);

        Assert.Equal(UpdateState.NotOurs, result.State);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public void An_update_edited_after_signing_is_refused()
    {
        var keys = NewKeys();
        using var _ = keys.Private;

        // Change the payload, keep the signature. This is what a hex editor
        // and a plausible-looking version number get you.
        var signed = Sign(Manifest(), keys.Private);
        var tampered = signed.Replace(
            Convert.ToBase64String(UpdateManifestFile.Serialise(Manifest()))[..20],
            Convert.ToBase64String(UpdateManifestFile.Serialise(Manifest(version: "9.9.9")))[..20],
            StringComparison.Ordinal);

        var result = Verify(tampered, keys.PublicKey);

        Assert.Equal(UpdateState.NotOurs, result.State);
    }

    [Fact]
    public void Anything_that_is_not_an_update_file_is_refused()
    {
        var keys = NewKeys();
        using var _ = keys.Private;

        Assert.Equal(UpdateState.Unreadable, Verify("hello", keys.PublicKey).State);
        Assert.Equal(UpdateState.Unreadable, Verify(null, keys.PublicKey).State);
        Assert.Equal(UpdateState.Unreadable, Verify("", keys.PublicKey).State);
    }

    /// <summary>
    /// The one that turns a signed release into a way to run anything on the
    /// machine. A manifest naming a path rather than a file name would, for
    /// as long as that signature stayed valid, let whoever holds a copy of it
    /// point the installer step at any executable on disk or on a share.
    /// </summary>
    [Theory]
    [InlineData(@"..\..\Windows\System32\cmd.exe")]
    [InlineData("../../../etc/passwd")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"\\attacker\share\payload.exe")]
    [InlineData("sub/folder/setup.exe")]
    public void A_manifest_naming_a_path_rather_than_a_file_is_refused(string installerFileName)
    {
        var keys = NewKeys();
        using var _ = keys.Private;

        // Correctly signed by us. The signature is not the defence here — the
        // shape check is, because a mistake in our own release process would
        // produce exactly this and would be signed.
        var result = Verify(Sign(Manifest(installerFileName: installerFileName), keys.Private), keys.PublicKey);

        Assert.Equal(UpdateState.Unreadable, result.State);
        Assert.Null(result.Manifest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("1.1")]
    public void A_manifest_without_a_usable_version_is_refused(string version)
    {
        var keys = NewKeys();
        using var _ = keys.Private;

        Assert.Equal(
            UpdateState.Unreadable,
            Verify(Sign(Manifest(version: version), keys.Private), keys.PublicKey).State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tooshort")]
    [InlineData("zzzz2013454044c47114e79279c2bd352c2b5583802b0c8ce338de45a3c4a04b")]
    public void A_manifest_without_a_usable_hash_is_refused(string sha256)
    {
        var keys = NewKeys();
        using var _ = keys.Private;

        Assert.Equal(
            UpdateState.Unreadable,
            Verify(Sign(Manifest(sha256: sha256), keys.Private), keys.PublicKey).State);
    }

    [Theory]
    [InlineData("1.0.0", UpdateState.NotNewer)]
    [InlineData("0.9.0", UpdateState.NotNewer)]
    [InlineData("1.0.1", UpdateState.Ready)]
    public void An_update_at_or_below_the_running_version_is_refused(string version, UpdateState expected)
    {
        var keys = NewKeys();
        using var _ = keys.Private;

        var result = Verify(Sign(Manifest(version: version), keys.Private), keys.PublicKey);

        Assert.Equal(expected, result.State);
    }

    [Fact]
    public void A_missing_installer_is_reported_as_missing_rather_than_altered()
    {
        var keys = NewKeys();
        using var _ = keys.Private;

        var result = Verify(
            Sign(Manifest(), keys.Private), keys.PublicKey, new FakeFiles { Exists = false });

        Assert.Equal(UpdateState.InstallerMissing, result.State);
        Assert.Contains("same folder", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_half_copied_installer_is_caught_on_size_without_hashing_it()
    {
        var keys = NewKeys();
        using var _ = keys.Private;
        var files = new FakeFiles { Size = Size / 2 };

        var result = Verify(Sign(Manifest(), keys.Private), keys.PublicKey, files);

        Assert.Equal(UpdateState.InstallerAltered, result.State);

        // The point of checking size first: a truncated file off a USB stick
        // is the common case, and hashing hundreds of megabytes to discover
        // what a length comparison already knew is a slow answer to a fast
        // question.
        Assert.Equal(0, files.HashCalls);
    }

    [Fact]
    public void An_installer_of_the_right_size_but_the_wrong_contents_is_refused()
    {
        var keys = NewKeys();
        using var _ = keys.Private;

        var result = Verify(
            Sign(Manifest(), keys.Private), keys.PublicKey,
            new FakeFiles { Hash = new string('b', 64) });

        Assert.Equal(UpdateState.InstallerAltered, result.State);
        Assert.Contains("Do not install it", result.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Hashing is work, and work done before the signature check is work done
    /// on the say-so of whoever wrote the file. A two hundred megabyte read
    /// off a USB stick is a denial of service that costs the attacker one
    /// file.
    /// </summary>
    [Fact]
    public void An_unsigned_update_is_refused_before_the_installer_is_read()
    {
        var ours = NewKeys();
        var theirs = NewKeys();
        using var _ = ours.Private;
        using var __ = theirs.Private;
        var files = new FakeFiles();

        Verify(Sign(Manifest(), theirs.Private), ours.PublicKey, files);

        Assert.Equal(0, files.HashCalls);
    }

    [Fact]
    public void A_build_with_no_public_key_trusts_nothing()
    {
        var keys = NewKeys();
        using var _ = keys.Private;

        // A development build, or a release built without the secret set.
        // It must refuse rather than accept, because the alternative is a
        // machine that runs whatever it is handed.
        var result = Verify(Sign(Manifest(), keys.Private), publicKey: "");

        Assert.Equal(UpdateState.NotOurs, result.State);
    }
}
