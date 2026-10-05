using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace HospitalPm.Infrastructure.Operations;

/// <summary>A file offered as a backup that cannot be used as one, and why, in words an administrator can act on.</summary>
public sealed class BackupUploadException(string message) : Exception(message);

/// <summary>A backup that cannot be opened, and why, in words an administrator can act on.</summary>
public sealed class BackupDecryptionException(string message, bool needsRecoveryKey = false) : Exception(message)
{
    /// <summary>
    /// True when the file is fine but this machine's own key does not open it (it was made on another
    /// machine, or the key here has been replaced), and the recovery key would.
    /// </summary>
    public bool NeedsRecoveryKey { get; } = needsRecoveryKey;
}

/// <summary>What the backup keys look like from outside, without revealing any of them.</summary>
public sealed record BackupKeyStatus(bool MasterKeyPresent, DateTime? RecoveryKeyCreatedAtUtc, bool RecoveryKeySaved);

/// <summary>The outcome of making a new recovery key.</summary>
public sealed record RecoveryKeyResult(string Key, int Rewrapped, int Skipped);

/// <summary>
/// Encrypts and decrypts the database backups.
///
/// A backup is the whole hospital's equipment records in one file, and the file is the part that leaves
/// the machine: copied to a USB drive, a shared folder, a cloud account. Encrypting it means the copy
/// is useless to whoever picks it up.
///
/// HOW
///
/// Each backup gets its own random 256-bit content key. The content key encrypts the file in 1 MiB
/// chunks with AES-256-GCM, and is itself stored in the file's header twice:
///
///   - under the MACHINE KEY, a random key kept in the locked-down data directory, so this machine
///     can restore its own backups without anyone typing anything;
///   - under the RECOVERY KEY, which an administrator writes down and keeps away from the machine, so a
///     backup can still be opened after the machine, and its machine key, are gone.
///
/// The recovery key is also kept on the machine, wrapped under the machine key. That is not a weakness:
/// whoever can read the machine key can already open every backup. It is what lets every NEW backup be
/// recoverable with the key the administrator wrote down, and what lets that key be replaced later.
///
/// Every chunk is authenticated, in order, and the last one is marked, so a flipped byte, a chunk
/// removed, two chunks swapped, a file cut short or extra bytes added are all refused rather than
/// handed to pg_restore.
///
/// WHAT IT DOES NOT DO
///
/// A key kept on the same drive as the backups protects nothing if the whole drive is taken. It protects
/// the copies that leave the machine, and a backup folder on a different drive from the key folder.
///
/// FORMAT, version 1 (all integers little-endian unless noted)
///
///   0   magic "HPBK"                                 4
///   4   version                                      1
///   5   file id (random; part of every chunk's AAD)  16
///   21  nonce prefix (random)                        8
///   29  chunk size                                   4
///   33  machine key id (first 8 bytes of SHA-256)    8
///   41  machine wrap: nonce 12, tag 16, content key 32   60
///   101 recovery key id                              8
///   109 recovery wrap                                60
///   169 body: repeated [flag 1][length 4][ciphertext][tag 16]
///
/// A chunk's nonce is the nonce prefix followed by its index (4 bytes, big-endian), and its additional
/// authenticated data is the file id, the index and the flag, so chunks cannot be moved between files or
/// within one. The flag is 1 on the final chunk only.
///
/// The two wraps are in the header and not in the chunks' authenticated data on purpose: replacing the
/// recovery key rewrites 68 bytes in place and never touches the body.
/// </summary>
public sealed class BackupVault
{
    public const int DefaultChunkSize = 1 << 20;
    public const string EncryptedExtension = ".enc";

    private const string MasterKeyFile = "backup-master.key";
    private const string RecoveryKeyFile = "backup-recovery.key";
    private const string RecoverySavedFile = "backup-recovery.saved";

    private static readonly byte[] Magic = "HPBK"u8.ToArray();
    private const byte Version = 1;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int WrapSize = NonceSize + TagSize + KeySize;       // 60
    private const int KeyIdSize = 8;
    private const int HeaderSize = 4 + 1 + 16 + 8 + 4 + KeyIdSize + WrapSize + KeyIdSize + WrapSize; // 169
    private const int RecoverySlotOffset = 4 + 1 + 16 + 8 + 4 + KeyIdSize + WrapSize;                 // 101
    private const int MaxChunkSize = 16 << 20;
    private const int MinChunkSize = 64;

    private readonly string _keyDirectory;
    private readonly int _chunkSize;
    private readonly ILogger<BackupVault>? _logger;
    private readonly object _gate = new();

    public BackupVault(string keyDirectory, ILogger<BackupVault>? logger = null, int chunkSize = DefaultChunkSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, MinChunkSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkSize, MaxChunkSize);

        _keyDirectory = keyDirectory;
        _chunkSize = chunkSize;
        _logger = logger;
    }

    // ------------------------------------------------------------------ keys

    public BackupKeyStatus Status()
    {
        lock (_gate)
        {
            var recovery = Path.Combine(_keyDirectory, RecoveryKeyFile);
            return new BackupKeyStatus(
                File.Exists(Path.Combine(_keyDirectory, MasterKeyFile)),
                File.Exists(recovery) ? File.GetLastWriteTimeUtc(recovery) : null,
                File.Exists(Path.Combine(_keyDirectory, RecoverySavedFile)));
        }
    }

    /// <summary>Creates the machine key and a first recovery key if there are none. Safe to call every time.</summary>
    public void EnsureKeys()
    {
        lock (_gate)
        {
            LoadKeys(create: true);
        }
    }

    /// <summary>
    /// Makes a new recovery key, replacing the old one, and re-wraps the recovery slot of every encrypted
    /// backup in <paramref name="backupDirectory"/> that this machine's key can open. The new key is returned
    /// once, formatted for writing down; it is not kept anywhere in a form that can be read without the
    /// machine key. A file this machine cannot open (made elsewhere) is left alone and counted.
    /// </summary>
    public RecoveryKeyResult CreateRecoveryKey(string? backupDirectory)
    {
        lock (_gate)
        {
            var (master, _) = LoadKeys(create: true);
            var recovery = RandomNumberGenerator.GetBytes(KeySize);

            int rewrapped = 0, skipped = 0;
            if (backupDirectory is not null && Directory.Exists(backupDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(backupDirectory, "*" + EncryptedExtension))
                {
                    if (RewrapRecovery(file, master, recovery)) rewrapped++;
                    else skipped++;
                }
            }

            WriteKeyFile(RecoveryKeyFile, WrapKey(master, recovery, [], "recovery-store"));
            var saved = Path.Combine(_keyDirectory, RecoverySavedFile);
            if (File.Exists(saved)) File.Delete(saved);

            if (_logger is not null)
            {
                _logger.LogWarning(
                    "A new backup recovery key was created. {Rewrapped} backups re-wrapped, {Skipped} could not be opened by this machine's key.",
                    rewrapped, skipped);
            }

            return new RecoveryKeyResult(FormatRecoveryKey(recovery), rewrapped, skipped);
        }
    }

    /// <summary>The administrator has written the recovery key down.</summary>
    public void MarkRecoveryKeySaved()
    {
        lock (_gate)
        {
            Directory.CreateDirectory(_keyDirectory);
            File.WriteAllText(Path.Combine(_keyDirectory, RecoverySavedFile), DateTime.UtcNow.ToString("o"));
        }
    }

    /// <summary>The recovery key as it is written down: 52 characters in groups of four.</summary>
    public static string FormatRecoveryKey(byte[] key)
    {
        var text = Base32.Encode(key);
        var groups = new List<string>();
        for (var i = 0; i < text.Length; i += 4)
        {
            groups.Add(text.Substring(i, Math.Min(4, text.Length - i)));
        }

        return string.Join('-', groups);
    }

    /// <summary>Reads a recovery key back, ignoring case, spaces and dashes. Throws if it is not one.</summary>
    public static byte[] ParseRecoveryKey(string text)
    {
        // The key is written on paper and typed back in on a bad day. The alphabet has no 0, 1 or 8, so a person who
        // wrote the letter O, I or B and later typed a zero, a one or an eight is understood, not refused.
        var cleaned = new string((text ?? string.Empty).Where(char.IsLetterOrDigit).ToArray())
            .ToUpperInvariant()
            .Replace('0', 'O')
            .Replace('1', 'I')
            .Replace('8', 'B');

        if (!Base32.TryDecode(cleaned, out var bytes) || bytes.Length != KeySize)
        {
            throw new BackupDecryptionException(
                "That is not a recovery key. It is 52 letters and numbers in groups of four, like ABCD-EFGH-IJKL-…");
        }

        return bytes;
    }

    // ------------------------------------------------------------------ files

    /// <summary>Whether a file on disk is one of these encrypted backups. Reads five bytes.</summary>
    public static bool LooksEncrypted(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[5];
            return stream.ReadAtLeast(head, 5, throwOnEndOfStream: false) == 5
                && head[..4].SequenceEqual(Magic) && head[4] == Version;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// A stream to write the plain backup into. Everything written comes out encrypted on
    /// <paramref name="destination"/>; disposing it writes the last chunk, which is what marks the file complete.
    /// </summary>
    public Stream CreateEncryptor(Stream destination, bool leaveOpen = false)
    {
        byte[] master, recovery;
        lock (_gate)
        {
            (master, recovery) = LoadKeys(create: true);
        }

        return new EncryptingStream(destination, master, recovery, _chunkSize, leaveOpen);
    }

    /// <summary>
    /// A stream that yields the plain backup from an encrypted one. The header is read and the key found
    /// before this returns; a bad chunk is reported as it is reached. Tries the machine key, then the recovery
    /// key if one is given.
    /// </summary>
    public Stream OpenDecryptor(Stream source, string? recoveryKey = null, bool leaveOpen = false)
    {
        byte[]? master = null;
        lock (_gate)
        {
            var masterPath = Path.Combine(_keyDirectory, MasterKeyFile);
            if (File.Exists(masterPath))
            {
                var bytes = File.ReadAllBytes(masterPath);
                if (bytes.Length == KeySize) master = bytes;
            }
        }

        var recovery = string.IsNullOrWhiteSpace(recoveryKey) ? null : ParseRecoveryKey(recoveryKey);
        return new DecryptingStream(source, master, recovery, leaveOpen);
    }

    /// <summary>Writes the encrypted backup, through a temporary name, so a half-written file never has the real one.</summary>
    public async Task EncryptFileAsync(string source, string destination, CancellationToken ct = default)
    {
        var partial = destination + ".partial";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            await using (var encryptor = CreateEncryptor(output, leaveOpen: true))
            {
                await input.CopyToAsync(encryptor, ct);
            }

            File.Move(partial, destination, overwrite: false);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    /// <summary>Writes the plain backup from an encrypted one, through a temporary name. For a restore, not for keeping.</summary>
    public async Task DecryptFileAsync(string source, string destination, string? recoveryKey = null, CancellationToken ct = default)
    {
        var partial = destination + ".partial";
        try
        {
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var decryptor = OpenDecryptor(input, recoveryKey, leaveOpen: true))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await decryptor.CopyToAsync(output, ct);
            }

            File.Move(partial, destination, overwrite: true);
        }
        finally
        {
            TryDelete(partial);
        }
    }

    // ------------------------------------------------------------------ key storage

    private (byte[] Master, byte[] Recovery) LoadKeys(bool create)
    {
        Directory.CreateDirectory(_keyDirectory);

        var masterPath = Path.Combine(_keyDirectory, MasterKeyFile);
        byte[] master;
        if (File.Exists(masterPath))
        {
            master = File.ReadAllBytes(masterPath);
            if (master.Length != KeySize)
            {
                // Refuse rather than replace it: a new machine key would lock this machine out of every
                // backup it has already made, and doing that quietly turns a damaged file into lost data.
                throw new InvalidOperationException(
                    $"The backup key at '{masterPath}' is damaged ({master.Length} bytes). "
                    + "Restore it from the copy kept with the installation, or open backups with the recovery key.");
            }
        }
        else if (create)
        {
            master = RandomNumberGenerator.GetBytes(KeySize);
            WriteKeyFile(MasterKeyFile, master);
            _logger?.LogInformation("Generated a new backup key in {Directory}", _keyDirectory);
        }
        else
        {
            throw new InvalidOperationException("There is no backup key on this machine.");
        }

        var recoveryPath = Path.Combine(_keyDirectory, RecoveryKeyFile);
        byte[] recovery;
        if (File.Exists(recoveryPath))
        {
            recovery = UnwrapKey(master, File.ReadAllBytes(recoveryPath), [], "recovery-store")
                ?? throw new InvalidOperationException(
                    $"The recovery key stored at '{recoveryPath}' cannot be opened with this machine's backup key. "
                    + "Create a new recovery key.");
        }
        else if (create)
        {
            recovery = RandomNumberGenerator.GetBytes(KeySize);
            WriteKeyFile(RecoveryKeyFile, WrapKey(master, recovery, [], "recovery-store"));
        }
        else
        {
            throw new InvalidOperationException("There is no recovery key on this machine.");
        }

        return (master, recovery);
    }

    /// <summary>Writes a key file that is never readable by anyone else, not even for an instant.</summary>
    private void WriteKeyFile(string name, byte[] contents)
    {
        var path = Path.Combine(_keyDirectory, name);
        var temp = path + ".new";

        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }

    // ------------------------------------------------------------------ wrapping

    private static byte[] KeyId(byte[] key) => SHA256.HashData(key)[..KeyIdSize];

    private static byte[] WrapKey(byte[] wrappingKey, byte[] contentKey, byte[] fileId, string label)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[KeySize];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(wrappingKey, TagSize);
        aes.Encrypt(nonce, contentKey, cipher, tag, WrapAad(label, fileId));

        var result = new byte[WrapSize];
        nonce.CopyTo(result, 0);
        tag.CopyTo(result, NonceSize);
        cipher.CopyTo(result, NonceSize + TagSize);
        return result;
    }

    private static byte[]? UnwrapKey(byte[] wrappingKey, byte[] wrapped, byte[] fileId, string label)
    {
        if (wrapped.Length != WrapSize) return null;

        var contentKey = new byte[KeySize];
        try
        {
            using var aes = new AesGcm(wrappingKey, TagSize);
            aes.Decrypt(
                wrapped.AsSpan(0, NonceSize),
                wrapped.AsSpan(NonceSize + TagSize, KeySize),
                wrapped.AsSpan(NonceSize, TagSize),
                contentKey,
                WrapAad(label, fileId));
            return contentKey;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static byte[] WrapAad(string label, byte[] fileId) => [.. Encoding.ASCII.GetBytes("HPBK1-" + label), .. fileId];

    /// <summary>Re-wraps the recovery slot of one file in place. False if this machine's key cannot open it.</summary>
    private static bool RewrapRecovery(string path, byte[] master, byte[] newRecovery)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var header = new byte[HeaderSize];
            if (stream.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) != HeaderSize) return false;
            if (!header.AsSpan(0, 4).SequenceEqual(Magic) || header[4] != Version) return false;

            var fileId = header.AsSpan(5, 16).ToArray();
            if (!header.AsSpan(33, KeyIdSize).SequenceEqual(KeyId(master))) return false;

            var contentKey = UnwrapKey(master, header.AsSpan(41, WrapSize).ToArray(), fileId, "master");
            if (contentKey is null) return false;

            var slot = new byte[KeyIdSize + WrapSize];
            KeyId(newRecovery).CopyTo(slot, 0);
            WrapKey(newRecovery, contentKey, fileId, "recovery").CopyTo(slot, KeyIdSize);

            stream.Position = RecoverySlotOffset;
            stream.Write(slot);
            stream.Flush(flushToDisk: true);
            CryptographicOperations.ZeroMemory(contentKey);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static byte[] ChunkNonce(byte[] prefix, uint counter)
    {
        var nonce = new byte[NonceSize];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), counter);
        return nonce;
    }

    private static byte[] ChunkAad(byte[] fileId, uint counter, byte flag)
    {
        var aad = new byte[fileId.Length + 5];
        fileId.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(fileId.Length), counter);
        aad[^1] = flag;
        return aad;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is not worth failing over; it is never the real backup.
        }
    }

    // ------------------------------------------------------------------ the streams

    private sealed class EncryptingStream : Stream
    {
        private readonly Stream _destination;
        private readonly bool _leaveOpen;
        private readonly AesGcm _aes;
        private readonly byte[] _fileId = RandomNumberGenerator.GetBytes(16);
        private readonly byte[] _noncePrefix = RandomNumberGenerator.GetBytes(8);
        private readonly byte[] _buffer;
        private int _filled;
        private uint _counter;
        private bool _finished;

        public EncryptingStream(Stream destination, byte[] master, byte[] recovery, int chunkSize, bool leaveOpen)
        {
            _destination = destination;
            _leaveOpen = leaveOpen;
            _buffer = new byte[chunkSize];

            var contentKey = RandomNumberGenerator.GetBytes(KeySize);
            _aes = new AesGcm(contentKey, TagSize);

            var header = new byte[HeaderSize];
            Magic.CopyTo(header, 0);
            header[4] = Version;
            _fileId.CopyTo(header, 5);
            _noncePrefix.CopyTo(header, 21);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(29), (uint)chunkSize);
            KeyId(master).CopyTo(header, 33);
            WrapKey(master, contentKey, _fileId, "master").CopyTo(header, 41);
            KeyId(recovery).CopyTo(header, RecoverySlotOffset);
            WrapKey(recovery, contentKey, _fileId, "recovery").CopyTo(header, RecoverySlotOffset + KeyIdSize);
            CryptographicOperations.ZeroMemory(contentKey);

            _destination.Write(header);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _destination.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(_finished, this);

            while (!data.IsEmpty)
            {
                // A full chunk is only sealed once more data has arrived, so the last chunk is always the one
                // sealed on dispose and is the only one marked final.
                if (_filled == _buffer.Length)
                {
                    SealChunk(final: false);
                }

                var take = Math.Min(data.Length, _buffer.Length - _filled);
                data[..take].CopyTo(_buffer.AsSpan(_filled));
                _filled += take;
                data = data[take..];
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            return ValueTask.CompletedTask;
        }

        private void SealChunk(bool final)
        {
            if (_counter == uint.MaxValue)
            {
                throw new InvalidOperationException("The backup is too large to encrypt in one file.");
            }

            var flag = final ? (byte)1 : (byte)0;
            var cipher = new byte[_filled];
            var tag = new byte[TagSize];

            _aes.Encrypt(ChunkNonce(_noncePrefix, _counter), _buffer.AsSpan(0, _filled), cipher, tag, ChunkAad(_fileId, _counter, flag));

            var frame = new byte[1 + 4 + _filled + TagSize];
            frame[0] = flag;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(1), (uint)_filled);
            cipher.CopyTo(frame, 5);
            tag.CopyTo(frame, 5 + _filled);
            _destination.Write(frame);

            _counter++;
            _filled = 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_finished)
            {
                _finished = true;
                try
                {
                    SealChunk(final: true);
                    _destination.Flush();
                }
                finally
                {
                    _aes.Dispose();
                    CryptographicOperations.ZeroMemory(_buffer);
                    if (!_leaveOpen) _destination.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    private sealed class DecryptingStream : Stream
    {
        private readonly Stream _source;
        private readonly bool _leaveOpen;
        private readonly AesGcm _aes;
        private readonly byte[] _fileId;
        private readonly byte[] _noncePrefix;
        private readonly int _chunkSize;
        private byte[] _plain = [];
        private int _plainPosition;
        private uint _counter;
        private bool _sawFinal;
        private bool _ended;

        public DecryptingStream(Stream source, byte[]? master, byte[]? recovery, bool leaveOpen)
        {
            _source = source;
            _leaveOpen = leaveOpen;

            var header = new byte[HeaderSize];
            if (source.ReadAtLeast(header, HeaderSize, throwOnEndOfStream: false) != HeaderSize
                || !header.AsSpan(0, 4).SequenceEqual(Magic))
            {
                throw new BackupDecryptionException("This is not an encrypted Hospital PM backup, or it is damaged.");
            }

            if (header[4] != Version)
            {
                throw new BackupDecryptionException(
                    $"This backup was made by a newer version of Hospital PM (format {header[4]}) and cannot be opened by this one.");
            }

            _fileId = header.AsSpan(5, 16).ToArray();
            _noncePrefix = header.AsSpan(21, 8).ToArray();
            _chunkSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(29));
            if (_chunkSize is < MinChunkSize or > MaxChunkSize)
            {
                throw new BackupDecryptionException("This backup's header is damaged.");
            }

            byte[]? contentKey = null;
            var damaged = false;

            if (master is not null && header.AsSpan(33, KeyIdSize).SequenceEqual(KeyId(master)))
            {
                contentKey = UnwrapKey(master, header.AsSpan(41, WrapSize).ToArray(), _fileId, "master");
                damaged = contentKey is null;
            }

            if (contentKey is null && recovery is not null)
            {
                if (header.AsSpan(RecoverySlotOffset, KeyIdSize).SequenceEqual(KeyId(recovery)))
                {
                    contentKey = UnwrapKey(recovery, header.AsSpan(RecoverySlotOffset + KeyIdSize, WrapSize).ToArray(), _fileId, "recovery");
                    damaged = damaged || contentKey is null;
                }
                else
                {
                    throw new BackupDecryptionException(
                        "That recovery key does not belong to this backup. A backup is opened by the recovery key that was current when it was made, or by a later one if it was re-wrapped.",
                        needsRecoveryKey: true);
                }
            }

            if (contentKey is null)
            {
                throw damaged
                    ? new BackupDecryptionException("This backup's header is damaged and its key cannot be opened.")
                    : new BackupDecryptionException(
                        recovery is null
                            ? "This machine's key does not open this backup. It was made on another machine, or the key here has been replaced. Enter the recovery key."
                            : "Neither this machine's key nor that recovery key opens this backup.",
                        needsRecoveryKey: true);
            }

            _aes = new AesGcm(contentKey, TagSize);
            CryptographicOperations.ZeroMemory(contentKey);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> destination)
        {
            if (destination.IsEmpty) return 0;

            while (_plainPosition >= _plain.Length)
            {
                if (_ended || !NextChunk()) return 0;
            }

            var take = Math.Min(destination.Length, _plain.Length - _plainPosition);
            _plain.AsSpan(_plainPosition, take).CopyTo(destination);
            _plainPosition += take;
            return take;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        /// <summary>Reads, checks and opens the next chunk. False at the true end of the file.</summary>
        private bool NextChunk()
        {
            if (_sawFinal)
            {
                // Nothing may follow the last chunk. Bytes appended to a backup are not part of it.
                if (_source.ReadByte() >= 0)
                {
                    throw new BackupDecryptionException("This backup has extra data after its end. It has been altered.");
                }

                _ended = true;
                return false;
            }

            var flagByte = _source.ReadByte();
            if (flagByte < 0)
            {
                throw new BackupDecryptionException(
                    "This backup is incomplete: the file ends before its last part. It was cut short, perhaps by a full disk or an interrupted copy.");
            }

            var flag = (byte)flagByte;
            if (flag > 1)
            {
                throw new BackupDecryptionException("This backup is damaged.");
            }

            Span<byte> lengthBytes = stackalloc byte[4];
            if (_source.ReadAtLeast(lengthBytes, 4, throwOnEndOfStream: false) != 4)
            {
                throw new BackupDecryptionException("This backup is incomplete: the file ends in the middle of a part.");
            }

            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
            if (length < 0 || length > _chunkSize)
            {
                throw new BackupDecryptionException("This backup is damaged.");
            }

            var cipher = new byte[length];
            var tag = new byte[TagSize];
            if (_source.ReadAtLeast(cipher, length, throwOnEndOfStream: false) != length
                || _source.ReadAtLeast(tag, TagSize, throwOnEndOfStream: false) != TagSize)
            {
                throw new BackupDecryptionException("This backup is incomplete: the file ends in the middle of a part.");
            }

            var plain = new byte[length];
            try
            {
                _aes.Decrypt(ChunkNonce(_noncePrefix, _counter), cipher, tag, plain, ChunkAad(_fileId, _counter, flag));
            }
            catch (CryptographicException)
            {
                throw new BackupDecryptionException(
                    $"This backup is damaged or has been altered: part {_counter + 1} does not check out.");
            }

            _plain = plain;
            _plainPosition = 0;
            _counter++;
            _sawFinal = flag == 1;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _aes.Dispose();
                if (!_leaveOpen) _source.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ Base32

    private static class Base32
    {
        private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        public static string Encode(byte[] data)
        {
            var result = new StringBuilder((data.Length * 8 + 4) / 5);
            int buffer = 0, bits = 0;
            foreach (var b in data)
            {
                buffer = (buffer << 8) | b;
                bits += 8;
                while (bits >= 5)
                {
                    result.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                    bits -= 5;
                }
            }

            if (bits > 0)
            {
                result.Append(Alphabet[(buffer << (5 - bits)) & 31]);
            }

            return result.ToString();
        }

        public static bool TryDecode(string text, out byte[] data)
        {
            var bytes = new List<byte>(text.Length * 5 / 8);
            int buffer = 0, bits = 0;
            foreach (var c in text)
            {
                var value = Alphabet.IndexOf(c);
                if (value < 0)
                {
                    data = [];
                    return false;
                }

                buffer = (buffer << 5) | value;
                bits += 5;
                if (bits >= 8)
                {
                    bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                    bits -= 8;
                }
            }

            data = [.. bytes];
            return true;
        }
    }
}
