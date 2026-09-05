using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace HospitalPm.Infrastructure.Identity;

/// <summary>
/// Supplies the JWT signing key, generating one on first run.
///
/// The key must not be a compile-time constant and must not live in a
/// committed config file: every hospital would then share the same signing
/// key, and anyone with a copy of the installer could mint valid tokens for
/// every install in the country. It is generated per install and never leaves
/// the machine.
///
/// Where it is stored is decided by the caller, and matters: an install test
/// found it sitting in Program Files, where BUILTIN\Users has read access by
/// inheritance, so every local user on a shared ward PC could read the key
/// that signs authentication tokens - and anyone who can read it can mint a
/// token for any user, including an administrator. Program.cs now passes the
/// keys folder inside the data directory, which the installer locks to SYSTEM
/// and Administrators before anything is written into it.
///
/// A file rather than a database row, deliberately: the app needs to
/// validate tokens during startup and while the database is unreachable,
/// and a key that lives in the database it protects is a bootstrapping
/// problem waiting to happen.
/// </summary>
public sealed partial class SigningKeyProvider
{
    private const string KeyFileName = "signing.key";
    private const int KeySizeBytes = 64; // 512-bit, comfortably above HS256 needs

    private readonly SymmetricSecurityKey _key;

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Generated a new JWT signing key at {Path}")]
    private static partial void LogKeyGenerated(ILogger logger, string path);

    public SigningKeyProvider(string dataDirectory, ILogger<SigningKeyProvider>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, KeyFileName);

        byte[] material;
        if (File.Exists(path))
        {
            material = File.ReadAllBytes(path);

            if (material.Length < 32)
            {
                // Refuse rather than silently regenerate: replacing the key
                // invalidates every issued token, and doing that quietly
                // turns a tampered file into a mass logout with no
                // explanation in the logs.
                throw new InvalidOperationException(
                    $"Signing key at '{path}' is too short ({material.Length} bytes). " +
                    "Refusing to start rather than regenerate, which would invalidate all issued tokens. " +
                    "Restore the file from backup, or delete it deliberately to issue a new key.");
            }
        }
        else
        {
            material = RandomNumberGenerator.GetBytes(KeySizeBytes);
            File.WriteAllBytes(path, material);
            RestrictPermissions(path);
            if (logger is not null)
            {
                LogKeyGenerated(logger, path);
            }
        }

        _key = new SymmetricSecurityKey(material);
    }

    public SymmetricSecurityKey Key => _key;

    private static void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // Owner read/write only. On Windows the file inherits the ACL of
            // the install directory, which the installer restricts.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
