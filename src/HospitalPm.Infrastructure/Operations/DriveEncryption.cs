using System.Diagnostics;

namespace HospitalPm.Infrastructure.Operations;

/// <summary>What could be found out about the drive that holds the data.</summary>
/// <param name="Encrypted">True, false, or null when it could not be told.</param>
/// <param name="Detail">One line, for the person reading the Diagnostics page.</param>
public sealed record DriveEncryptionResult(bool? Encrypted, string Detail);

/// <summary>
/// Whether the drive that holds the data is encrypted, as far as the operating system will say.
///
/// PostgreSQL has no encryption of its own files, so the honest protection for the database on disk is the drive's:
/// BitLocker on Windows, LUKS on Linux. This only asks. It does not turn anything on and it does not claim more than
/// it was told: when it cannot tell (not allowed to ask, a container, a tool that is missing) it says that it could
/// not tell, rather than guess either way.
/// </summary>
public static class DriveEncryption
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public static DriveEncryptionResult Check(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return CheckWindows(path);
            }

            if (OperatingSystem.IsLinux())
            {
                return CheckLinux(path);
            }

            return new DriveEncryptionResult(null, "This operating system is not one the check knows how to ask.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or ArgumentException)
        {
            return new DriveEncryptionResult(null, $"Could not be asked: {e.Message}");
        }
    }

    // ---------------------------------------------------------------- Windows

    private static DriveEncryptionResult CheckWindows(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new DriveEncryptionResult(null, "The data is on a network share, which this check cannot ask about.");
        }

        var drive = root.TrimEnd('\\');
        var manage = Path.Combine(Environment.SystemDirectory, "manage-bde.exe");
        if (!File.Exists(manage))
        {
            return new DriveEncryptionResult(null, "BitLocker's tool is not on this edition of Windows, so it could not be asked.");
        }

        var (exit, output) = Run(manage, ["-status", drive]);
        return ParseBitLocker(exit, output, drive);
    }

    /// <summary>Reads what <c>manage-bde -status</c> printed. Public for the tests, which feed it real output.</summary>
    public static DriveEncryptionResult ParseBitLocker(int exitCode, string output, string drive)
    {
        if (exitCode != 0)
        {
            // Almost always "run as administrator": the installed service is LocalSystem and can ask, a person
            // running it from a desktop may not. Said as that, never as "not encrypted".
            return new DriveEncryptionResult(null, "BitLocker would not say. The service must run as an administrator account to ask.");
        }

        var lines = output.Split('\n').Select(l => l.Trim()).ToList();
        string? Value(string label) => lines
            .FirstOrDefault(l => l.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            ?.Split(':', 2).ElementAtOrDefault(1)?.Trim();

        var protection = Value("Protection Status");
        var conversion = Value("Conversion Status");

        if (protection is null && conversion is null)
        {
            return new DriveEncryptionResult(null, "BitLocker's answer was not in a form this check understands.");
        }

        // "Protection On" / "Protection Off". Ends with, not contains: "Protection" has "on" inside it.
        if (protection?.EndsWith(" On", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new DriveEncryptionResult(true, $"{drive} is protected by BitLocker ({conversion ?? "protection on"}).");
        }

        // Encrypted but with protection suspended (a firmware update, say) still means the key is on the disk in the clear.
        if (conversion?.Contains("Fully Decrypted", StringComparison.OrdinalIgnoreCase) == true)
        {
            return new DriveEncryptionResult(false, $"{drive} is not encrypted by BitLocker.");
        }

        return new DriveEncryptionResult(false, $"{drive} is not protected by BitLocker right now ({protection ?? conversion}).");
    }

    // ---------------------------------------------------------------- Linux

    private static DriveEncryptionResult CheckLinux(string path)
    {
        if (File.Exists("/.dockerenv"))
        {
            return new DriveEncryptionResult(null, "This runs in a container, whose storage is the host's. Check the host's drive instead.");
        }

        var full = Path.GetFullPath(path);
        var (mountExit, source) = Run("findmnt", ["-n", "-o", "SOURCE", "--target", full]);
        if (mountExit != 0 || string.IsNullOrWhiteSpace(source))
        {
            return new DriveEncryptionResult(null, "The drive holding the data could not be found out.");
        }

        // The device and everything it is stacked on. A LUKS volume appears in that chain as type "crypt".
        var (listExit, chain) = Run("lsblk", ["-s", "-n", "-o", "TYPE", source.Trim().Split('\n')[0]]);
        return ParseLsblk(listExit, chain, source.Trim());
    }

    /// <summary>Reads <c>lsblk -s -n -o TYPE</c>. Public for the tests.</summary>
    public static DriveEncryptionResult ParseLsblk(int exitCode, string output, string device)
    {
        if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return new DriveEncryptionResult(null, "The drive's layers could not be listed.");
        }

        var types = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return types.Contains("crypt", StringComparer.OrdinalIgnoreCase)
            ? new DriveEncryptionResult(true, $"{device} sits on an encrypted (LUKS) volume.")
            : new DriveEncryptionResult(false, $"{device} is not on an encrypted volume.");
    }

    // ---------------------------------------------------------------- running a tool

    private static (int Exit, string Output) Run(string exe, string[] arguments)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{exe} could not be started.");
        var output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(Timeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }

            return (-1, string.Empty);
        }

        return (process.ExitCode, output.GetAwaiter().GetResult());
    }
}
