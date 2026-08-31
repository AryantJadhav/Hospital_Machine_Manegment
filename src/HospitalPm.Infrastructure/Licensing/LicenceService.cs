using HospitalPm.Domain.Licensing;
using Microsoft.Extensions.Options;

namespace HospitalPm.Infrastructure.Licensing;

public sealed class LicenceOptions
{
    public const string Section = "Licence";

    /// <summary>
    /// Our signing key's public half, base64 SubjectPublicKeyInfo, produced by
    /// the vendor licence tool's keygen command. Empty in source: a release
    /// build sets it, and the private half never enters the repository.
    /// </summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Licence file location. Relative paths resolve against the binary's own
    /// directory rather than the working directory, which for a Windows
    /// Service is system32.
    /// </summary>
    public string Path { get; set; } = "hospitalpm.licence";
}

/// <summary>
/// The installation's licence, read from disk and checked offline.
///
/// Deliberately re-read rather than cached for the life of the process: an
/// administrator who installs a renewal expects the banner to clear without
/// restarting a service that a ward is using. The file is small and this is
/// only called from admin screens and diagnostics.
/// </summary>
public sealed class LicenceService(IOptions<LicenceOptions> options, TimeProvider clock)
{
    private readonly LicenceOptions _options = options.Value;

    public string ResolvePath() =>
        System.IO.Path.IsPathRooted(_options.Path)
            ? _options.Path
            : System.IO.Path.Combine(AppContext.BaseDirectory, _options.Path);

    public LicenceStatus Current()
    {
        if (string.IsNullOrWhiteSpace(_options.PublicKey))
        {
            // A build with no public key cannot validate anything. Saying so
            // beats reporting every licence as forged.
            return new LicenceStatus(LicenceState.Missing, null,
                "This build has no licence key configured, so licensing is not enforced.");
        }

        var path = ResolvePath();

        string? text = null;
        if (File.Exists(path))
        {
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new LicenceStatus(LicenceState.Invalid, null,
                    "The licence file could not be read from disk. "
                    + "Check the service account's permissions on the installation folder.");
            }
        }

        return new LicenceVerifier(_options.PublicKey)
            .Verify(text, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));
    }

    /// <summary>
    /// Installs a licence file, but only if it verifies.
    ///
    /// Refusing to save a bad licence means the installed file is always one
    /// that worked at least once, so a hospital cannot overwrite a good
    /// licence with a truncated email attachment and lose both.
    /// </summary>
    public (bool Saved, LicenceStatus Status) Install(string fileText)
    {
        if (string.IsNullOrWhiteSpace(_options.PublicKey))
        {
            return (false, new LicenceStatus(LicenceState.Missing, null,
                "This build has no licence key configured, so a licence cannot be installed."));
        }

        var status = new LicenceVerifier(_options.PublicKey)
            .Verify(fileText, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime));

        // Expired licences are still installable: a hospital renewing after a
        // lapse should be able to put the old file back while the new one is
        // being issued, and it is honest about what it is.
        if (status.State is LicenceState.Invalid or LicenceState.Missing)
        {
            return (false, status);
        }

        try
        {
            var path = ResolvePath();
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, fileText);
            return (true, status);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (false, new LicenceStatus(LicenceState.Invalid, null,
                "The licence is valid but could not be saved. "
                + "Check the service account's permissions on the installation folder."));
        }
    }
}
