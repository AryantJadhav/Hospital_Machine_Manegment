using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HospitalPm.Domain.Operations;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace HospitalPm.Infrastructure.Operations;

/// <summary>
/// Takes a backup of the database with pg_dump, reads it back, and records
/// what happened.
///
/// The read-back is the point. A dump that was written but cannot be restored
/// is the failure this whole feature exists to prevent, and it is invisible
/// until the day someone needs it. pg_restore --list parses the archive's
/// table of contents, which catches a truncated or corrupt file for the cost
/// of a second or two.
///
/// Runs as a Hangfire job in the same process, so a client install still has
/// two services and no extra daemon.
/// </summary>
public sealed partial class BackupService(
    HospitalPmDbContext db,
    PgToolLocator locator,
    BackupVault vault,
    IConfiguration configuration,
    IOptions<BackupOptions> options,
    TimeProvider clock,
    HospitalPm.Infrastructure.Maintenance.HospitalClock hospital,
    ILogger<BackupService> logger,
    DriveSync? drive = null)
{
    private readonly BackupOptions _options = options.Value;

    /// <summary>Hangfire entry point. Never throws: a failed backup is a recorded row.</summary>
    public Task RunScheduledAsync(CancellationToken ct = default) =>
        RunAsync(BackupTrigger.Scheduled, ct);

    public async Task<BackupRun> RunAsync(BackupTrigger trigger, CancellationToken ct = default)
    {
        var startedAt = clock.GetUtcNow().UtcDateTime;
        var stopwatch = Stopwatch.StartNew();

        var run = new BackupRun
        {
            StartedAtUtc = startedAt,
            Status = BackupStatus.Running,
            Trigger = trigger,
        };

        db.BackupRuns.Add(run);
        await db.SaveChangesAsync(ct);

        try
        {
            await ExecuteAsync(run, ct);
            run.Status = BackupStatus.Succeeded;
        }
        catch (Exception e)
        {
            // Deliberately broad. Anything that stops a backup — a full disk,
            // a missing tool, a killed child process — has to end as a row
            // saying so rather than an unobserved background exception.
            Log.Failed(logger, e);
            run.Status = BackupStatus.Failed;
            run.Error = Describe(e);
        }
        finally
        {
            stopwatch.Stop();
            run.FinishedAtUtc = clock.GetUtcNow().UtcDateTime;
            run.DurationMs = (int)stopwatch.ElapsedMilliseconds;
            await db.SaveChangesAsync(CancellationToken.None);
        }

        // The night's backup goes off the machine too, when that is switched on. Only the nightly one: "Back up now"
        // answers a person who is waiting, and has "Upload now" for the drive. Never fails the backup that has
        // just succeeded, whatever happens to the upload.
        if (run.Status == BackupStatus.Succeeded && trigger == BackupTrigger.Scheduled && drive is not null)
        {
            try
            {
                await drive.SyncAsync(ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.DriveFailed(logger, e);
            }
        }

        return run;
    }

    private async Task ExecuteAsync(BackupRun run, CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString("HospitalPm");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("No database connection is configured.");
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var serverVersion = await ReadServerVersionAsync(connectionString, ct);
        run.ServerVersion = serverVersion?.ToString();

        var pgDump = locator.FindPgDump(serverVersion);
        if (!pgDump.IsUsable)
        {
            throw new InvalidOperationException(pgDump.Problem ?? "pg_dump is unavailable.");
        }

        var directory = ResolveDirectory();
        Directory.CreateDirectory(directory);

        // Sortable, unambiguous, and safe on both filesystems, on the
        // hospital's clock so the name agrees with the time shown beside it, and
        // ending in the zone so it is never a guess. Names written before this
        // carry a UTC stamp and no zone; both still match the pruning pattern,
        // and a newer file always sorts after an older one because the hospital
        // clock is ahead of UTC, never behind it.
        var stamp = (run.StartedAtUtc + hospital.Offset).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var plainName = $"hospitalpm-{stamp}-{Reports.ReportTime.Zone(hospital.Offset)}.dump";
        // Always encrypted: there is no setting that writes a plain backup.
        var fileName = plainName + BackupVault.EncryptedExtension;
        var fullPath = Path.Combine(directory, fileName);

        // Custom format: compressed, and pg_restore can pull single tables out
        // of it. Plain SQL would be larger and all-or-nothing to restore.
        var arguments = new[]
        {
            "--format=custom",
            "--no-owner",
            "--no-privileges",
            $"--host={builder.Host}",
            $"--port={builder.Port}",
            $"--username={builder.Username}",
            $"--dbname={builder.Database}",
        };

        // pg_dump writes to its standard output and the bytes go straight through the encryption into the file. The
        // plain backup is never on the disk, not even for a moment.
        vault.EnsureKeys();
        var written = await DumpEncryptedAsync(pgDump.Path!, arguments, builder.Password, fullPath, ct);
        if (written == 0)
        {
            throw new InvalidOperationException(
                "pg_dump reported success but wrote no data. Check free disk space on the backup drive.");
        }

        var info = new FileInfo(fullPath);
        if (!info.Exists || info.Length == 0)
        {
            throw new InvalidOperationException(
                "pg_dump reported success but wrote no data. Check free disk space on the backup drive.");
        }

        run.FileName = fileName;
        run.SizeBytes = info.Length;

        if (_options.VerifyAfterWrite)
        {
            await VerifyEncryptedAsync(fullPath, serverVersion, ct);
        }

        Prune(directory);

        // A backup that has just succeeded must not be failed by tidying up after it.
        try
        {
            await EncryptLeftoversAsync(ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.LeftoversFailed(logger, e);
        }
    }

    /// <summary>
    /// Runs pg_dump with its output going straight through the encryption into the file, and says how many
    /// plain bytes it produced. The file is written under a temporary name and only given its real one when
    /// pg_dump has finished and succeeded, so a failed dump never leaves something that looks like a backup.
    /// </summary>
    private async Task<long> DumpEncryptedAsync(
        string exePath, string[] arguments, string? password, string destination, CancellationToken ct)
    {
        var partial = destination + ".partial";
        long plainBytes = 0;

        try
        {
            var startInfo = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            if (!string.IsNullOrEmpty(password))
            {
                startInfo.Environment["PGPASSWORD"] = password;
            }

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("pg_dump could not be started.");

            var stderr = new StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) stderr.AppendLine(e.Data);
            };
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(_options.TimeoutMinutes));

            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await using (var encryptor = vault.CreateEncryptor(file, leaveOpen: true))
            {
                try
                {
                    var counting = new CountingStream(encryptor);
                    await process.StandardOutput.BaseStream.CopyToAsync(counting, timeout.Token);
                    plainBytes = counting.Count;
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    TryKill(process);
                    throw new InvalidOperationException(
                        $"pg_dump did not finish within {_options.TimeoutMinutes} minutes and was stopped.");
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                    throw;
                }
                catch (Exception)
                {
                    // A full disk, say. Stopped here, pg_dump would wait for ever on a pipe nobody reads,
                    // holding its connection to the database.
                    TryKill(process);
                    throw;
                }
            }

            ThrowIfFailed(process, "pg_dump", stderr);

            File.Move(partial, destination, overwrite: false);
            return plainBytes;
        }
        finally
        {
            try
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Never the real backup, and not worth failing over.
            }
        }
    }

    /// <summary>
    /// Reads the finished encrypted backup back. First every part is decrypted and checked, which proves the
    /// file is whole and has not been touched; then the decrypted bytes are given to pg_restore --list, which
    /// proves the archive inside can be opened. A backup nobody can open is not a backup.
    /// </summary>
    private async Task VerifyEncryptedAsync(string fullPath, Version? serverVersion, CancellationToken ct, string? recoveryKey = null)
    {
        await using (var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        await using (var decryptor = vault.OpenDecryptor(input, recoveryKey, leaveOpen: true))
        {
            await decryptor.CopyToAsync(Stream.Null, ct);
        }

        var pgRestore = locator.FindPgRestore(serverVersion);
        if (!pgRestore.IsUsable)
        {
            // The file is written and every part of it has checked out. Refusing to record it because the
            // archive checker is missing would throw away a good backup.
            Log.NotVerified(logger, pgRestore.Problem);
            return;
        }

        var startInfo = new ProcessStartInfo(pgRestore.Path!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("--list");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("pg_restore could not be started.");

        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) stderr.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();
        _ = process.StandardOutput.ReadToEndAsync(ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(_options.TimeoutMinutes));

        try
        {
            await using (var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var decryptor = vault.OpenDecryptor(input, recoveryKey, leaveOpen: true))
            {
                try
                {
                    await decryptor.CopyToAsync(process.StandardInput.BaseStream, timeout.Token);
                }
                catch (IOException)
                {
                    // pg_restore --list has read what it needs (the archive's table of contents) and gone.
                    // Whether that was a success is what its exit code says.
                }
            }

            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // Already closed by the other end.
            }

            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new InvalidOperationException(
                $"pg_restore did not finish within {_options.TimeoutMinutes} minutes and was stopped.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        ThrowIfFailed(process, "pg_restore", stderr);
    }

    /// <summary>
    /// Encrypts any plain backup left in the backup folder: the dumps made before encryption was on, and the
    /// safety copy the restore script writes of the database it is about to replace. Each is encrypted, read
    /// back and compared with the original, and only then is the plain file deleted. A file written in the
    /// last half minute is left alone in case it is still being written. Returns how many were done.
    /// </summary>
    public async Task<int> EncryptLeftoversAsync(CancellationToken ct = default)
    {
        var directory = ResolveDirectory();
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var folder = new DirectoryInfo(directory);
        var plain = folder.EnumerateFiles("hospitalpm-*.dump")
            .Concat(folder.EnumerateFiles("pre-restore-*.dump"))
            .Where(f => f.LastWriteTimeUtc < clock.GetUtcNow().UtcDateTime.AddSeconds(-30))
            .ToList();

        var done = 0;
        foreach (var file in plain)
        {
            var target = file.FullName + BackupVault.EncryptedExtension;
            if (File.Exists(target))
            {
                continue;
            }

            try
            {
                vault.EnsureKeys();
                await vault.EncryptFileAsync(file.FullName, target, ct);

                if (!await SameContentAsync(file.FullName, target, ct))
                {
                    File.Delete(target);
                    Log.LeftoverMismatch(logger, file.Name);
                    continue;
                }

                var size = new FileInfo(target).Length;
                var name = file.Name;
                file.Delete();

                await db.BackupRuns
                    .Where(r => r.FileName == name)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.FileName, name + BackupVault.EncryptedExtension)
                        .SetProperty(r => r.SizeBytes, (long?)size), ct);

                Log.LeftoverEncrypted(logger, name);
                done++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or BackupDecryptionException)
            {
                Log.LeftoverFailed(logger, file.Name, e);
                try
                {
                    if (File.Exists(target)) File.Delete(target);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    // Left for the next pass.
                }
            }
        }

        return done;
    }

    /// <summary>Whether decrypting <paramref name="encrypted"/> gives back exactly <paramref name="plain"/>.</summary>
    private async Task<bool> SameContentAsync(string plain, string encrypted, CancellationToken ct)
    {
        byte[] before;
        await using (var input = new FileStream(plain, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true))
        {
            before = await SHA256.HashDataAsync(input, ct);
        }

        byte[] after;
        await using (var input = new FileStream(encrypted, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        await using (var decryptor = vault.OpenDecryptor(input, leaveOpen: true))
        {
            after = await SHA256.HashDataAsync(decryptor, ct);
        }

        return before.AsSpan().SequenceEqual(after);
    }

    private static void ThrowIfFailed(Process process, string toolName, StringBuilder stderr)
    {
        if (process.ExitCode == 0)
        {
            return;
        }

        var detail = stderr.ToString().Trim();
        throw new InvalidOperationException(
            detail.Length > 0
                ? $"{toolName} failed: {Shorten(detail)}"
                : $"{toolName} failed with exit code {process.ExitCode}.");
    }

    /// <summary>Counts what passes through, so an empty dump is noticed.</summary>
    private sealed class CountingStream(Stream inner) : Stream
    {
        public long Count { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            Count += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            Count += buffer.Length;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException
                                      or System.ComponentModel.Win32Exception)
        {
            // Already gone, or not ours to kill.
        }
    }

    /// <summary>
    /// Deletes the oldest dumps beyond the retention count.
    ///
    /// Only files this app named are considered. A hospital that points the
    /// backup directory at a shared drive must not have its other files
    /// deleted by us.
    /// </summary>
    private void Prune(string directory)
    {
        if (_options.RetainCount <= 0) return;

        try
        {
            var folder = new DirectoryInfo(directory);
            var ours = folder.EnumerateFiles("hospitalpm-*.dump")
                .Concat(folder.EnumerateFiles("hospitalpm-*.dump" + BackupVault.EncryptedExtension))
                .OrderByDescending(f => f.Name, StringComparer.Ordinal)
                .Skip(_options.RetainCount)
                .ToList();

            foreach (var file in ours)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log.PruneFileFailed(logger, file.Name, e);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Retention failing must never fail the backup that just succeeded.
            Log.PruneFailed(logger, directory, e);
        }
    }

    /// <summary>
    /// Takes in a backup file the administrator carried here (from a USB drive, a download, another machine), so it
    /// can be restored. The bytes are written to a temporary name and checked before anything is kept: it must be an
    /// encrypted Hospital PM backup, every part must decrypt (with this machine's key, or the recovery key given),
    /// and the archive inside must open. Only then is it given its place in the backup folder and a row in the
    /// history. A file that fails any check is deleted, and nothing about it is recorded.
    ///
    /// The file is kept exactly as it came, so it still opens with the recovery key it was made with.
    /// </summary>
    /// <exception cref="BackupUploadException">The file is not a usable backup. The message says why.</exception>
    /// <exception cref="BackupDecryptionException">This machine's key does not open it and no usable recovery key was given.</exception>
    public async Task<BackupRun> AdoptAsync(Stream content, string? originalName, string? recoveryKey, CancellationToken ct = default)
    {
        var started = clock.GetUtcNow().UtcDateTime;
        var directory = ResolveDirectory();
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $"upload-{Guid.NewGuid():N}.partial");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await content.CopyToAsync(output, ct);
            }

            if (!BackupVault.LooksEncrypted(temporary))
            {
                throw new BackupUploadException(
                    "That is not an encrypted Hospital PM backup. Choose a file whose name ends in .dump.enc, "
                    + "as downloaded from the Backups page.");
            }

            var connectionString = configuration.GetConnectionString("HospitalPm");
            var serverVersion = string.IsNullOrWhiteSpace(connectionString)
                ? null
                : await ReadServerVersionAsync(connectionString, ct);

            try
            {
                await VerifyEncryptedAsync(temporary, serverVersion, ct, recoveryKey);
            }
            catch (InvalidOperationException e)
            {
                throw new BackupUploadException("The file opened, but the archive inside it is damaged: " + Shorten(e.Message));
            }

            var finalName = UploadedName(directory, originalName);
            File.Move(temporary, Path.Combine(directory, finalName));

            var run = new BackupRun
            {
                StartedAtUtc = started,
                FinishedAtUtc = clock.GetUtcNow().UtcDateTime,
                Status = BackupStatus.Succeeded,
                Trigger = BackupTrigger.Manual,
                FileName = finalName,
                SizeBytes = new FileInfo(Path.Combine(directory, finalName)).Length,
                ServerVersion = serverVersion?.ToString(),
                DurationMs = (int)(clock.GetUtcNow().UtcDateTime - started).TotalMilliseconds,
            };
            db.BackupRuns.Add(run);
            await db.SaveChangesAsync(ct);
            return run;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
                // Already moved into place, or still held: the next startup tidy and retention deal with it.
            }
        }
    }

    /// <summary>
    /// A name for a file brought in from outside. The original is kept when it is one of ours and is free;
    /// otherwise a fresh one in the same form. Never a name taken from the request as it came: it is only
    /// ever used when it matches the pattern of the files this program writes.
    /// </summary>
    private string UploadedName(string directory, string? originalName)
    {
        var name = Path.GetFileName(originalName ?? string.Empty);
        if (UploadedFileName().IsMatch(name) && !File.Exists(Path.Combine(directory, name)))
        {
            return name;
        }

        var stamp = clock.GetUtcNow().UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (var attempt = 0; ; attempt++)
        {
            name = $"hospitalpm-{stamp}-uploaded{(attempt == 0 ? string.Empty : "-" + attempt.ToString(CultureInfo.InvariantCulture))}.dump{BackupVault.EncryptedExtension}";
            if (!File.Exists(Path.Combine(directory, name)))
            {
                return name;
            }
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^hospitalpm-[0-9]{8}-[0-9]{6}-[A-Za-z0-9+-]{1,10}\.dump\.enc$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex UploadedFileName();

    public string ResolveDirectory() =>
        Path.IsPathRooted(_options.Directory)
            ? _options.Directory
            : Path.Combine(AppContext.BaseDirectory, _options.Directory);

    private static async Task<Version?> ReadServerVersionAsync(string connectionString, CancellationToken ct)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);
            return connection.PostgreSqlVersion;
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Turns an exception into something a hospital IT contact can act on.
    /// The log keeps the stack trace; the dashboard gets a sentence.
    /// </summary>
    private static string Describe(Exception e) => e switch
    {
        InvalidOperationException => Shorten(e.Message),
        UnauthorizedAccessException => "Access denied writing to the backup directory. "
                                       + "Check the service account's permissions.",
        IOException io when io.Message.Contains("space", StringComparison.OrdinalIgnoreCase)
            => "The backup drive is out of space.",
        IOException => $"Could not write the backup file: {Shorten(e.Message)}",
        OperationCanceledException => "The backup was cancelled, most likely by a service restart.",
        _ => Shorten(e.Message),
    };

    private static string Shorten(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 500 ? collapsed : collapsed[..500] + "…";
    }

    /// <summary>
    /// Source-generated log messages. Backups run unattended, so the log is
    /// the only account of what happened on the nights nobody looked.
    /// </summary>
    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Sending the backup to the drive failed")]
        public static partial void DriveFailed(ILogger logger, Exception e);

        [LoggerMessage(Level = LogLevel.Error, Message = "Backup failed")]
        public static partial void Failed(ILogger logger, Exception e);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Backup written but not verified: {Problem}")]
        public static partial void NotVerified(ILogger logger, string? problem);

        [LoggerMessage(Level = LogLevel.Information, Message = "Encrypted the plain backup {File}")]
        public static partial void LeftoverEncrypted(ILogger logger, string file);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not encrypt the plain backup {File}; it is left as it was")]
        public static partial void LeftoverFailed(ILogger logger, string file, Exception e);

        [LoggerMessage(Level = LogLevel.Warning, Message = "The encrypted copy of {File} did not match it, so the plain file was kept")]
        public static partial void LeftoverMismatch(ILogger logger, string file);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Tidying plain backups after a backup failed")]
        public static partial void LeftoversFailed(ILogger logger, Exception e);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete old backup {File}")]
        public static partial void PruneFileFailed(ILogger logger, string file, Exception e);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Could not prune old backups in {Directory}")]
        public static partial void PruneFailed(ILogger logger, string directory, Exception e);
    }
}
