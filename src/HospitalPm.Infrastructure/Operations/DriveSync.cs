using System.Diagnostics;
using System.Text.Json;
using HospitalPm.Domain.Licensing;
using HospitalPm.Infrastructure.Licensing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HospitalPm.Infrastructure.Operations;

/// <summary>What one run of rclone said.</summary>
public sealed record RcloneResult(int Exit, string Output, string Error);

/// <summary>Runs rclone. A seam so the sending logic is tested without a network, and rclone itself is tested separately.</summary>
public interface IRcloneRunner
{
    Task<RcloneResult> RunAsync(
        string exe,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken ct);
}

/// <summary>The real thing: rclone as a child process that runs once and exits. Never a service, never left running.</summary>
public sealed class ProcessRcloneRunner : IRcloneRunner
{
    public async Task<RcloneResult> RunAsync(
        string exe,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken ct)
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

        // The remote, and its key, are given to rclone through its own environment and no other way: never in a file
        // left on the disk, and never on a command line, which any local user can read.
        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("rclone could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return new RcloneResult(-1, string.Empty, $"rclone did not finish within {timeout.TotalMinutes:0} minutes and was stopped.");
        }

        return new RcloneResult(process.ExitCode, await output, await error);
    }
}

/// <summary>What the Backups page shows about the drive.</summary>
public sealed record DriveStatus(
    bool Enabled,
    bool Ready,
    string? Problem,
    string? RclonePath,
    string? Folder,
    DateTime? LastUploadAtUtc,
    DateTime? LastAttemptAtUtc,
    string? LastError,
    int Uploaded,
    int Pending);

/// <summary>What one sync did.</summary>
public sealed record DriveSyncResult(bool Ran, int Sent, string? Error, string? Skipped);

/// <summary>
/// Sends the encrypted backups to a cloud drive with rclone, and keeps only the newest few there.
///
/// Four rules hold the design:
///
///  - Only encrypted files are ever sent. A plain dump is the whole database in the clear and is refused here
///    whatever calls this.
///  - It never fails a backup, and never throws. No internet means the files wait for the next night.
///  - It is off unless switched on, and does nothing without a current licence (the folder is named by it).
///  - It does not try to be clever: no sync, no conflict handling. It sends the files that have not gone yet, newest
///    first, and removes the oldest beyond the number to keep. What has gone is remembered in a small file beside
///    the backups.
///
/// The key for the drive is given to rclone in its environment for the one command and nowhere else.
/// </summary>
public sealed class DriveSync(
    IOptions<BackupOptions> options,
    LicenceService licences,
    IRcloneRunner runner,
    TimeProvider clock,
    ILogger<DriveSync> logger)
{
    /// <summary>The remote built from the settings. Its name only matters inside the environment passed to rclone.</summary>
    private const string BuiltRemoteName = "HPDRIVE";

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    private readonly BackupOptions _options = options.Value;

    private DriveOptions Drive => _options.Drive;

    private bool UsesConfigFile => !string.IsNullOrWhiteSpace(Drive.RcloneConfigFile);

    /// <summary>The remote rclone is told to use: one from the person's own rclone.conf, or the one built here.</summary>
    private string RemoteLabel => UsesConfigFile ? Drive.RemoteName!.Trim() : BuiltRemoteName;

    private string StatePath => Path.Combine(_options.ResolveDirectory(), ".drive-state.json");

    // ------------------------------------------------------------------ what is needed

    /// <summary>Everything needed to send, or the reason it cannot be, in words for the person looking at the page.</summary>
    private (string? Rclone, string? Credentials, string? Folder, string? Problem) Prepare()
    {
        if (!Drive.Enabled)
        {
            return (null, null, null, "Google Drive backup is switched off.");
        }

        if (licences.CurrentLock() is not null)
        {
            return (null, null, null, "This installation is locked.");
        }

        // The licence is needed to name the folder. A folder named in the settings does not need one: that is a
        // person pointing the program at their own drive, not a hospital using the built-in account.
        var licence = licences.Current();
        var licensed = licence.State is LicenceState.Valid or LicenceState.Expired && licence.Licence is not null;
        if (!licensed && string.IsNullOrWhiteSpace(Drive.Folder))
        {
            return (null, null, null,
                "Backup to Google Drive needs a licence, because the drive folder is named after it. Install one, or set Backup:Drive:Folder.");
        }

        var rclone = FindRclone();
        if (rclone is null)
        {
            return (null, null, null, "rclone was not found. Put rclone beside the program, or set Backup:Drive:RclonePath.");
        }

        var credentials = ReadSecret(Drive.TokenJson, Drive.TokenFile) ?? ReadSecret(Drive.ServiceAccountJson, Drive.ServiceAccountFile);
        if (UsesConfigFile)
        {
            if (!File.Exists(Drive.RcloneConfigFile))
            {
                return (rclone, null, null, $"There is no rclone configuration file at {Drive.RcloneConfigFile}.");
            }

            if (string.IsNullOrWhiteSpace(Drive.RemoteName))
            {
                return (rclone, null, null, "Backup:Drive:RemoteName is not set: the name of the remote in that rclone.conf, e.g. gdrive.");
            }
        }
        else if (credentials is null && !Drive.Remote.ContainsKey("type"))
        {
            return (rclone, null, null,
                "Google Drive is not signed in. Set Backup:Drive:TokenJson (a personal account) or Backup:Drive:ServiceAccountJson.");
        }

        var folder = string.IsNullOrWhiteSpace(Drive.Folder)
            ? licence.Licence!.LicenceId.ToString("N")
            : Drive.Folder.Trim().TrimEnd('/');
        return (rclone, credentials, folder, null);
    }

    /// <summary>A secret given as text or as a file. Whitespace around it is not part of it.</summary>
    private static string? ReadSecret(string? text, string? file)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            return text.Trim();
        }

        try
        {
            return !string.IsNullOrWhiteSpace(file) && File.Exists(file)
                ? File.ReadAllText(file).Trim()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? FindRclone()
    {
        var name = OperatingSystem.IsWindows() ? "rclone.exe" : "rclone";

        if (!string.IsNullOrWhiteSpace(Drive.RclonePath))
        {
            return File.Exists(Drive.RclonePath) ? Drive.RclonePath : null;
        }

        var beside = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(beside))
        {
            return beside;
        }

        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>The remote, as rclone's own environment settings, so nothing about it is written down anywhere.</summary>
    private Dictionary<string, string> RemoteEnvironment(string? credentials)
    {
        // The person's own rclone.conf holds the remote: nothing is built, and nothing is put in the environment.
        if (UsesConfigFile)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal) { ["RCLONE_CONFIG"] = Drive.RcloneConfigFile!.Trim() };
        }

        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (credentials is not null)
        {
            settings["type"] = "drive";

            if (IsToken(credentials))
            {
                // A personal account's own sign-in, with only the right to see what this program put there.
                settings["scope"] = "drive.file";
                settings["token"] = credentials;
            }
            else
            {
                settings["scope"] = "drive";
                settings["service_account_credentials"] = credentials;
            }

            if (!string.IsNullOrWhiteSpace(Drive.RootFolderId))
            {
                settings["root_folder_id"] = Drive.RootFolderId.Trim();
            }
        }

        foreach (var (key, value) in Drive.Remote)
        {
            settings[key] = value;
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // No configuration file: the remote is entirely in the environment, and rclone is not to go looking for one.
            ["RCLONE_CONFIG"] = Path.Combine(Path.GetTempPath(), "hospitalpm-no-rclone-config"),
        };
        foreach (var (key, value) in settings)
        {
            env[$"RCLONE_CONFIG_{BuiltRemoteName}_{key.ToUpperInvariant()}"] = value;
        }

        return env;
    }

    // ------------------------------------------------------------------ the files

    /// <summary>A token has an access token and a refresh token; a service account key has a private key.</summary>
    private static bool IsToken(string secret) =>
        secret.Contains("\"access_token\"", StringComparison.Ordinal) || secret.Contains("\"refresh_token\"", StringComparison.Ordinal);

    private static bool IsOurs(string name) =>
        name.StartsWith("hospitalpm-", StringComparison.Ordinal)
        && name.EndsWith(".dump" + BackupVault.EncryptedExtension, StringComparison.Ordinal);

    private IEnumerable<string> EncryptedBackups()
    {
        var directory = _options.ResolveDirectory();
        if (!Directory.Exists(directory))
        {
            return [];
        }

        // Newest first. The names sort by time, and the hospital's clock is never behind UTC.
        return Directory.EnumerateFiles(directory, "hospitalpm-*.dump" + BackupVault.EncryptedExtension)
            .Where(path => IsOurs(Path.GetFileName(path)))
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .Take(Math.Max(1, Drive.KeepCount));
    }

    // ------------------------------------------------------------------ the state

    private sealed class State
    {
        public List<string> Uploaded { get; set; } = [];

        public DateTime? LastUploadAtUtc { get; set; }

        public DateTime? LastAttemptAtUtc { get; set; }

        public string? LastError { get; set; }
    }

    private State ReadState()
    {
        try
        {
            return File.Exists(StatePath)
                ? JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath)) ?? new State()
                : new State();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Lost memory means a file is sent again, which is harmless. Never a reason to fail.
            return new State();
        }
    }

    private void WriteState(State state)
    {
        try
        {
            Directory.CreateDirectory(_options.ResolveDirectory());
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Could not remember what has gone to the drive");
        }
    }

    // ------------------------------------------------------------------ what the page shows

    public DriveStatus Status()
    {
        var (rclone, _, folder, problem) = Prepare();
        var state = ReadState();
        var current = EncryptedBackups().Select(Path.GetFileName).ToList();

        return new DriveStatus(
            Enabled: Drive.Enabled,
            Ready: problem is null,
            Problem: problem,
            RclonePath: rclone,
            Folder: folder,
            LastUploadAtUtc: state.LastUploadAtUtc,
            LastAttemptAtUtc: state.LastAttemptAtUtc,
            LastError: state.LastError,
            Uploaded: current.Count(n => state.Uploaded.Contains(n!)),
            Pending: current.Count(n => !state.Uploaded.Contains(n!)));
    }

    // ------------------------------------------------------------------ sending

    /// <summary>
    /// Sends what has not gone yet, newest first, then removes the oldest beyond the number to keep. Safe to call at
    /// any time and from anywhere: one at a time, and it does nothing when there is nothing to do.
    /// </summary>
    public async Task<DriveSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var (rclone, credentials, folder, problem) = Prepare();
        if (problem is not null || rclone is null || folder is null)
        {
            return new DriveSyncResult(false, 0, null, problem);
        }

        if (!await OneAtATime.WaitAsync(0, ct))
        {
            return new DriveSyncResult(false, 0, null, "An upload is already running.");
        }

        try
        {
            var state = ReadState();
            state.LastAttemptAtUtc = clock.GetUtcNow().UtcDateTime;
            var env = RemoteEnvironment(credentials);
            var timeout = TimeSpan.FromMinutes(Math.Max(1, Drive.TimeoutMinutes));
            var sent = 0;
            string? error = null;

            foreach (var path in EncryptedBackups().Where(p => !state.Uploaded.Contains(Path.GetFileName(p))))
            {
                var name = Path.GetFileName(path);

                // Refused whatever called this: a file that is not encrypted never leaves.
                if (!BackupVault.LooksEncrypted(path))
                {
                    logger.LogWarning("Not sending {File} to the drive: it is not encrypted", name);
                    continue;
                }

                var result = await runner.RunAsync(
                    rclone, ["copyto", path, $"{RemoteLabel}:{folder}/{name}", "--checksum", "--retries", "2", "--low-level-retries", "5"],
                    env, timeout, ct);

                if (result.Exit != 0)
                {
                    error = Describe(result, credentials);
                    break;
                }

                // Checked, not trusted. "rclone exited zero" is good evidence and this is the proof: every byte's size and
                // hash on the drive is compared with the file here, and only a match counts as sent.
                var verified = await runner.RunAsync(
                    rclone, ["check", Path.GetDirectoryName(path)!, $"{RemoteLabel}:{folder}", "--one-way", "--include", name],
                    env, timeout, ct);

                if (verified.Exit != 0)
                {
                    error = "It was uploaded but did not match when checked, so it is not counted as sent. " + Describe(verified, credentials);
                    break;
                }

                state.Uploaded.Add(name);
                state.LastUploadAtUtc = clock.GetUtcNow().UtcDateTime;
                sent++;
            }

            state.LastError = error;

            // Tidying the drive is best effort and never changes the outcome.
            if (error is null)
            {
                await TrimAsync(rclone, env, folder, timeout, ct);
            }

            // Only the files still on the machine need remembering.
            var here = EncryptedBackups().Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
            state.Uploaded = [.. state.Uploaded.Where(here.Contains)];
            WriteState(state);

            return new DriveSyncResult(true, sent, error, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            // Never fails the backup that called it.
            logger.LogWarning(e, "Sending backups to the drive failed");
            var state = ReadState();
            state.LastAttemptAtUtc = clock.GetUtcNow().UtcDateTime;
            state.LastError = Redact(e.Message, ReadSecret(Drive.TokenJson, Drive.TokenFile) ?? ReadSecret(Drive.ServiceAccountJson, Drive.ServiceAccountFile));
            WriteState(state);
            return new DriveSyncResult(true, 0, state.LastError, null);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>Keeps the newest few on the drive and removes the rest. Only files this program named are ever touched.</summary>
    private async Task TrimAsync(string rclone, Dictionary<string, string> env, string folder, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var listing = await runner.RunAsync(rclone, ["lsf", $"{RemoteLabel}:{folder}", "--files-only"], env, timeout, ct);
            if (listing.Exit != 0)
            {
                return;
            }

            var surplus = listing.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(IsOurs)
                .OrderByDescending(n => n, StringComparer.Ordinal)
                .Skip(Math.Max(1, Drive.KeepCount));

            foreach (var name in surplus)
            {
                await runner.RunAsync(rclone, ["deletefile", $"{RemoteLabel}:{folder}/{name}"], env, timeout, ct);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(e, "Could not tidy the drive");
        }
    }

    private static string Describe(RcloneResult result, string? credentials)
    {
        var text = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()
                   ?? "rclone failed with no message.";
        return Redact(line, credentials);
    }

    /// <summary>Whatever rclone printed, with the key taken out, in case it ever echoes one, and short enough to show.</summary>
    private static string Redact(string text, string? credentials)
    {
        if (!string.IsNullOrEmpty(credentials))
        {
            text = text.Replace(credentials, "[key removed]", StringComparison.Ordinal);
        }

        // A token, if rclone ever prints one: the values of the two that matter.
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"""(access_token|refresh_token)""\s*:\s*""[^""]*""", "\"$1\":\"[key removed]\"",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        text = System.Text.RegularExpressions.Regex.Replace(
            text, "-----BEGIN [A-Z ]*PRIVATE KEY-----.*?-----END [A-Z ]*PRIVATE KEY-----", "[key removed]",
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        return text.Length <= 400 ? text : text[..400] + "…";
    }
}
