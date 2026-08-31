using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace HospitalPm.Infrastructure.Operations;

/// <summary>The outcome of looking for one PostgreSQL command-line tool.</summary>
/// <param name="Path">Full path to the executable, or null when none was usable.</param>
/// <param name="Version">Version reported by the tool itself.</param>
/// <param name="Problem">Operator-readable reason it cannot be used, or null when it can.</param>
public sealed record PgTool(string? Path, Version? Version, string? Problem)
{
    public bool IsUsable => Path is not null && Problem is null;
}

/// <summary>
/// Finds pg_dump and pg_restore on the machine.
///
/// These ship with PostgreSQL, which is already one of the two services a
/// client install is allowed, so this adds nothing to the install. It does
/// mean the app cannot assume a path: PostgreSQL lands in Program Files on
/// Windows and in a version-numbered directory on Debian, and neither is
/// necessarily on the service account's PATH.
///
/// A tool older than the server is treated as unusable rather than tried and
/// hoped for. pg_dump refuses outright against a newer server, and the point
/// of failing here is that the reason reaches the dashboard instead of
/// arriving as an exit code nobody reads.
/// </summary>
public sealed partial class PgToolLocator(IOptions<BackupOptions> options)
{
    private readonly BackupOptions _options = options.Value;

    /// <summary>
    /// Upper bound on how many candidate binaries are launched to read a
    /// version. A long PATH on a hospital PC should not make this slow.
    /// </summary>
    private const int MaxCandidatesExamined = 12;

    [GeneratedRegex(@"(\d+)\.(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public PgTool FindPgDump(Version? serverVersion) =>
        Find("pg_dump", _options.PgDumpPath, serverVersion);

    public PgTool FindPgRestore(Version? serverVersion) =>
        Find("pg_restore", _options.PgRestorePath, serverVersion);

    private static PgTool Find(string toolName, string? configured, Version? serverVersion)
    {
        var exe = OperatingSystem.IsWindows() ? toolName + ".exe" : toolName;

        // A configured path is taken at its word: if an administrator pointed
        // at something that is not there, saying so beats silently searching
        // and backing up with a different binary than they intended.
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured)
                ? Check(configured, serverVersion, toolName)
                : new PgTool(null, null,
                    $"{toolName} was configured as '{configured}' but no file is there.");
        }

        // Every candidate is examined, not just the first one found. A machine
        // upgraded from PostgreSQL 16 to 17 typically keeps the old client
        // first on PATH while the new one sits in a version-numbered
        // directory; stopping at the first hit would refuse to back up a
        // server that has a perfectly good client installed.
        PgTool? bestProblem = null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var examined = 0;

        foreach (var candidate in CandidatePaths(exe))
        {
            if (!File.Exists(candidate) || !seen.Add(Path.GetFullPath(candidate)))
            {
                continue;
            }

            // Each check starts a process. A pathological PATH must not turn
            // finding pg_dump into a minute of process launches.
            if (++examined > MaxCandidatesExamined) break;

            var tool = Check(candidate, serverVersion, toolName);
            if (tool.IsUsable) return tool;

            // Keep the most informative failure. "Version 16, server is 17"
            // tells an administrator what to install; "did not report a
            // version" does not.
            if (bestProblem is null || (bestProblem.Version is null && tool.Version is not null))
            {
                bestProblem = tool;
            }
        }

        return bestProblem ?? new PgTool(null, null,
            $"{toolName} could not be found. It is installed with PostgreSQL; " +
            $"set Backup:{(toolName == "pg_dump" ? "PgDumpPath" : "PgRestorePath")} " +
            "to its full path.");
    }

    private static IEnumerable<string> CandidatePaths(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim().Trim('"');
            if (trimmed.Length == 0) continue;

            string combined;
            try
            {
                combined = Path.Combine(trimmed, exe);
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry must not take the whole search down.
                continue;
            }

            yield return combined;
        }

        if (OperatingSystem.IsWindows())
        {
            // Newest first, so a machine with two versions installed uses the
            // one that can talk to the newer server.
            foreach (var root in new[]
                     {
                         Environment.GetEnvironmentVariable("ProgramW6432"),
                         Environment.GetEnvironmentVariable("ProgramFiles"),
                         @"C:\Program Files",
                     })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;

                var pgRoot = Path.Combine(root, "PostgreSQL");
                if (!Directory.Exists(pgRoot)) continue;

                foreach (var versionDir in Directory.EnumerateDirectories(pgRoot)
                             .OrderByDescending(d => ParseLeadingInt(Path.GetFileName(d))))
                {
                    yield return Path.Combine(versionDir, "bin", exe);
                }
            }
        }
        else
        {
            foreach (var fixedDir in new[] { "/usr/bin", "/usr/local/bin", "/opt/homebrew/bin" })
            {
                yield return Path.Combine(fixedDir, exe);
            }

            // Debian and Ubuntu keep per-major-version directories here.
            const string debianRoot = "/usr/lib/postgresql";
            if (Directory.Exists(debianRoot))
            {
                foreach (var versionDir in Directory.EnumerateDirectories(debianRoot)
                             .OrderByDescending(d => ParseLeadingInt(Path.GetFileName(d))))
                {
                    yield return Path.Combine(versionDir, "bin", exe);
                }
            }
        }
    }

    private static int ParseLeadingInt(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var digits = new string(text.TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    private static PgTool Check(string path, Version? serverVersion, string toolName)
    {
        var version = ReadVersion(path);

        if (version is null)
        {
            return new PgTool(null, null,
                $"{path} did not report a version, so it cannot be used for backups.");
        }

        // pg_dump refuses to dump from a server newer than itself. Catching it
        // here turns a nightly failure into something the diagnostics page can
        // state plainly.
        if (serverVersion is not null && version.Major < serverVersion.Major)
        {
            return new PgTool(path, version,
                $"{toolName} is version {version.Major} but the database server is " +
                $"{serverVersion.Major}. Install PostgreSQL {serverVersion.Major} client tools.");
        }

        return new PgTool(path, version, null);
    }

    private static Version? ReadVersion(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null) return null;

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000)) return null;

            var match = VersionPattern().Match(output);
            return match.Success
                ? new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value))
                : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
