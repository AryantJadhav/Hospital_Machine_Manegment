using System.Globalization;
using System.Security.Cryptography;

namespace HospitalPm.Domain.Updates;

/// <summary>
/// Decides whether an update file may be installed, entirely offline.
///
/// ECDSA over P-256 with SHA-256, the same algorithm the licence uses and for
/// the same reasons — in the base class library, no new dependency, no call
/// out to anything. The key is a different one, because this signature
/// authorises code that will run as LocalSystem on a hospital's machine and a
/// licence signature does not. Someone who can issue licences should not
/// thereby be able to push code to every install.
///
/// This class can only verify. The private half lives in a vendor-side tool
/// that is never shipped.
///
/// Order matters and is deliberate: signature first, then the file. Hashing
/// two hundred megabytes off a USB stick before knowing whether the manifest
/// is even ours would be work done on an attacker's say-so.
/// </summary>
public sealed class UpdateVerifier(string publicKeyBase64)
{
    /// <summary>
    /// Checks one update file.
    /// </summary>
    /// <param name="manifestPath">Full path to the .update file, used only for reporting.</param>
    /// <param name="manifestText">Its contents.</param>
    /// <param name="folder">Where to look for the installer it names.</param>
    /// <param name="runningVersion">
    /// The version currently installed. An update at or below it is refused:
    /// re-running an old installer over a newer database is how a hospital
    /// ends up with a schema its binary does not understand.
    /// </param>
    /// <param name="files">Filesystem access, so this stays testable without touching a disk.</param>
    public UpdateCandidate Verify(
        string manifestPath,
        string? manifestText,
        string folder,
        Version runningVersion,
        IUpdateFileSystem files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var parsed = manifestText is null ? null : UpdateManifestFile.Parse(manifestText);
        if (parsed is null)
        {
            return Fail(UpdateState.Unreadable, manifestPath,
                "This is not an update file, or it was damaged on the way here. "
                + "Copy it again from the original.");
        }

        if (!SignatureIsGood(parsed))
        {
            // Deliberately does not distinguish "edited" from "signed by
            // someone else". Both mean do not run it, and saying more only
            // helps whoever is experimenting.
            return Fail(UpdateState.NotOurs, manifestPath,
                "This update was not issued by us, or it was changed after it was issued. "
                + "Do not install it. Ask your supplier to send it again.");
        }

        var manifest = UpdateManifestFile.Deserialise(parsed.Payload);
        if (manifest is null || !IsWellFormed(manifest, out var version))
        {
            return Fail(UpdateState.Unreadable, manifestPath,
                "This update file is signed but this version of the software cannot read it. "
                + "It was probably issued for a newer release.");
        }

        if (version <= runningVersion)
        {
            return new UpdateCandidate(UpdateState.NotNewer, manifest, manifestPath, null,
                version == runningVersion
                    ? $"Version {manifest.Version} is already installed."
                    : $"Version {manifest.Version} is older than the installed {runningVersion.ToString(3)}.");
        }

        var installerPath = files.Combine(folder, manifest.InstallerFileName);
        if (!files.FileExists(installerPath))
        {
            return new UpdateCandidate(UpdateState.InstallerMissing, manifest, manifestPath, null,
                $"{manifest.InstallerFileName} is not in the same folder as this update file. "
                + "Both files have to be copied across together.");
        }

        // Size first. It is free, and a half-copied file off a USB stick is
        // the common case rather than the exotic one.
        var size = files.FileSize(installerPath);
        if (size != manifest.SizeBytes)
        {
            return new UpdateCandidate(UpdateState.InstallerAltered, manifest, manifestPath, installerPath,
                $"{manifest.InstallerFileName} is {Megabytes(size)} but should be "
                + $"{Megabytes(manifest.SizeBytes)}. The copy did not finish — copy it again.");
        }

        if (!string.Equals(files.Sha256Hex(installerPath), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new UpdateCandidate(UpdateState.InstallerAltered, manifest, manifestPath, installerPath,
                $"{manifest.InstallerFileName} is not the file this update was issued for. "
                + "Do not install it. Copy it again from the original, and if it still fails, "
                + "ask your supplier to send it again.");
        }

        return new UpdateCandidate(UpdateState.Ready, manifest, manifestPath, installerPath,
            $"Version {manifest.Version} is ready to install.");
    }

    /// <summary>
    /// Rejects anything the rest of the flow would have to defend against
    /// later. A signed manifest is trusted from here on, so this is where the
    /// shape gets checked rather than at the point of use.
    /// </summary>
    private static bool IsWellFormed(UpdateManifest manifest, out Version version)
    {
        version = new Version(0, 0, 0);

        if (string.IsNullOrWhiteSpace(manifest.Version)) return false;
        if (!Version.TryParse(manifest.Version, out var parsed)) return false;
        if (parsed.Major < 0 || parsed.Minor < 0 || parsed.Build < 0) return false;

        // A bare file name and nothing else. A manifest that could name a
        // path — "..\..\Windows\System32\cmd.exe", a UNC share — would turn
        // one signed release into a way to run any executable on the machine,
        // for as long as that signature stays valid.
        if (string.IsNullOrWhiteSpace(manifest.InstallerFileName)) return false;
        if (manifest.InstallerFileName != Path.GetFileName(manifest.InstallerFileName)) return false;
        if (manifest.InstallerFileName.Contains("..", StringComparison.Ordinal)) return false;

        if (manifest.SizeBytes <= 0) return false;
        if (manifest.Sha256 is not { Length: 64 }) return false;
        if (!manifest.Sha256.All(Uri.IsHexDigit)) return false;

        version = parsed;
        return true;
    }

    private bool SignatureIsGood(UpdateManifestFile.Parsed parsed)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);

            return ecdsa.VerifyData(
                parsed.Payload, parsed.Signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            // A malformed signature, or a public key this build cannot parse.
            // Either way, not trustworthy.
            return false;
        }
    }

    private static UpdateCandidate Fail(UpdateState state, string path, string message) =>
        new(state, null, path, null, message);

    private static string Megabytes(long bytes) =>
        (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
}

/// <summary>
/// The three things verification needs from a disk. An interface so the
/// verifier's rules can be tested without staging hundreds of megabytes, and
/// so the hashing implementation stays in one place.
/// </summary>
public interface IUpdateFileSystem
{
    string Combine(string folder, string fileName);
    bool FileExists(string path);
    long FileSize(string path);
    string Sha256Hex(string path);
}
