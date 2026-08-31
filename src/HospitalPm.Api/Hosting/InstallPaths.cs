namespace HospitalPm.Api.Hosting;

/// <summary>
/// Where an installed service keeps the things it must not lose.
///
/// Deliberately not the install directory. Program Files is readable by every
/// local user, and the connection string kept here carries a database
/// password; the uninstaller also has to be able to remove the program
/// without removing the hospital's backups and licence.
/// </summary>
public static class InstallPaths
{
    /// <summary>
    /// Overrides the data directory. The installer sets this for the service
    /// so a hospital can put data on a different drive.
    /// </summary>
    public const string EnvironmentVariable = "HOSPITALPM_DATA";

    public static string DataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        if (OperatingSystem.IsWindows())
        {
            // C:\ProgramData\Hospital PM — survives uninstall and roams to no
            // user profile, which is what a service running as LocalSystem
            // needs.
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Hospital PM");
        }

        return "/var/lib/hospitalpm";
    }

    /// <summary>
    /// Machine-specific settings written by the installer: the connection
    /// string, the port, the licence public key. Optional, because a
    /// development run has none of it.
    /// </summary>
    public static string SettingsFile() => Path.Combine(DataDirectory(), "appsettings.json");
}
