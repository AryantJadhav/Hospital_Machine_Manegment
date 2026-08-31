namespace HospitalPm.Api.Hosting;

/// <summary>
/// Where an installed service keeps the things it must not lose: the
/// connection string, the JWT signing key, the licence, the backups.
///
/// Deliberately not the install directory. Program Files is readable by every
/// local user and the signing key must not be — anyone who can read it can
/// mint a token for any user. The uninstaller also has to be able to remove
/// the program without removing the hospital's data.
/// </summary>
public static class InstallPaths
{
    /// <summary>
    /// Overrides the data directory. The installer sets this for the service
    /// so a hospital can put data on a different drive.
    /// </summary>
    public const string EnvironmentVariable = "HOSPITALPM_DATA";

    private static readonly Lazy<Resolved> Location = new(Resolve, isThreadSafe: true);

    private sealed record Resolved(string Path, bool IsFallback);

    public static string DataDirectory() => Location.Value.Path;

    /// <summary>
    /// True when the preferred system location could not be used and the
    /// binary's own directory is standing in. Normal for a development run;
    /// on a real install it means the signing key is somewhere less protected
    /// than it should be, so the caller logs a warning.
    /// </summary>
    public static bool UsingFallback => Location.Value.IsFallback;

    /// <summary>
    /// Machine-specific settings written by the installer. Optional, because
    /// a development run has none of it.
    /// </summary>
    public static string SettingsFile() => Path.Combine(DataDirectory(), "appsettings.json");

    private static Resolved Resolve()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // An explicit choice is honoured as given. Silently using
            // somewhere else would hide a misconfiguration on the one machine
            // where it matters.
            return new Resolved(configured, IsFallback: false);
        }

        var preferred = OperatingSystem.IsWindows()
            // C:\ProgramData\Hospital PM — survives uninstall, belongs to no
            // user profile, and is always writable by the LocalSystem account
            // the service runs as.
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Hospital PM")
            : "/var/lib/hospitalpm";

        if (IsUsable(preferred))
        {
            return new Resolved(preferred, IsFallback: false);
        }

        // Nothing here is writable on a developer's machine or in CI, where
        // /var/lib belongs to root and the process is not root. Falling back
        // beside the binary keeps `dotnet run` and the test host working; an
        // installed service never reaches this branch, because the installer
        // creates the directory and grants the service account access.
        return new Resolved(Path.Combine(AppContext.BaseDirectory, "data"), IsFallback: true);
    }

    /// <summary>
    /// Creating the directory is not enough — an existing one can be
    /// unwritable — so this writes and deletes a probe file.
    /// </summary>
    private static bool IsUsable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);

            var probe = Path.Combine(path, $".probe-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
