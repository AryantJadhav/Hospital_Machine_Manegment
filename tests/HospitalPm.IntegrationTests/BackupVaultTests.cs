using System.Security.Cryptography;
using HospitalPm.Infrastructure.Operations;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The backup encryption on its own, with no database: that what goes in comes out, that nothing readable is
/// left in between, and that every way of damaging a file or using the wrong key is refused rather than
/// handed on.
/// </summary>
public sealed class BackupVaultTests : IDisposable
{
    // Small parts, so a few hundred bytes cross several part boundaries.
    private const int Chunk = 64;

    private readonly List<string> _directories = [];

    public void Dispose()
    {
        foreach (var directory in _directories)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover temp folder is not worth failing a test run.
            }
        }
    }

    private string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "hospitalpm-vault-tests", Guid.NewGuid().ToString("N"));
        _directories.Add(path);
        return path;
    }

    private BackupVault NewVault(int chunk = Chunk) => new(NewDirectory(), chunkSize: chunk);

    private static byte[] Encrypt(BackupVault vault, byte[] plain)
    {
        using var output = new MemoryStream();
        using (var stream = vault.CreateEncryptor(output, leaveOpen: true))
        {
            stream.Write(plain);
        }

        return output.ToArray();
    }

    private static byte[] Decrypt(BackupVault vault, byte[] encrypted, string? recoveryKey = null)
    {
        using var input = new MemoryStream(encrypted);
        using var stream = vault.OpenDecryptor(input, recoveryKey, leaveOpen: true);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Random(int length) => RandomNumberGenerator.GetBytes(length);

    private const int HeaderSize = 169;

    // ---------------------------------------------------------------- what goes in comes out

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Chunk - 1)]
    [InlineData(Chunk)]
    [InlineData(Chunk + 1)]
    [InlineData(Chunk * 3)]
    [InlineData(1000)]
    public void What_is_encrypted_comes_back_exactly_for_any_size_including_nothing_and_exact_parts(int length)
    {
        var vault = NewVault();
        var plain = Random(length);

        Assert.Equal(plain, Decrypt(vault, Encrypt(vault, plain)));
    }

    [Fact]
    public void A_large_backup_round_trips_in_the_normal_part_size()
    {
        var vault = NewVault(BackupVault.DefaultChunkSize);
        var plain = Random((5 << 20) + 123);

        var back = Decrypt(vault, Encrypt(vault, plain));

        Assert.Equal(SHA256.HashData(plain), SHA256.HashData(back));
    }

    [Fact]
    public void Writing_in_awkward_pieces_gives_the_same_result()
    {
        var vault = NewVault();
        var plain = Random(777);

        using var output = new MemoryStream();
        using (var stream = vault.CreateEncryptor(output, leaveOpen: true))
        {
            var rest = plain.AsSpan();
            foreach (var size in new[] { 1, 7, 100, 63, 65, 3 })
            {
                stream.Write(rest[..size]);
                rest = rest[size..];
            }

            stream.Write(rest);
        }

        Assert.Equal(plain, Decrypt(vault, output.ToArray()));
    }

    [Fact]
    public void Nothing_readable_is_left_in_the_encrypted_file_and_two_encryptions_differ()
    {
        var vault = NewVault();
        var plain = "PGDMP equipment_type Hospital Name "u8.ToArray().Concat(new byte[300]).ToArray();

        var first = Encrypt(vault, plain);
        var second = Encrypt(vault, plain);

        Assert.False(first.AsSpan().IndexOf("PGDMP"u8) >= 0, "the archive's own marker is visible in the encrypted file");
        Assert.False(first.AsSpan().IndexOf("equipment_type"u8) >= 0, "plain text is visible in the encrypted file");
        Assert.NotEqual(first, second);
        Assert.Equal(Decrypt(vault, first), Decrypt(vault, second));
    }

    [Fact]
    public void An_encrypted_file_is_recognised_and_a_plain_one_is_not()
    {
        var vault = NewVault();
        var directory = NewDirectory();
        Directory.CreateDirectory(directory);

        var encrypted = Path.Combine(directory, "a.dump.enc");
        File.WriteAllBytes(encrypted, Encrypt(vault, Random(100)));
        var plain = Path.Combine(directory, "b.dump");
        File.WriteAllBytes(plain, "PGDMP\u0001\u000e\u0000"u8.ToArray());
        var empty = Path.Combine(directory, "c.dump");
        File.WriteAllBytes(empty, []);

        Assert.True(BackupVault.LooksEncrypted(encrypted));
        Assert.False(BackupVault.LooksEncrypted(plain));
        Assert.False(BackupVault.LooksEncrypted(empty));
        Assert.False(BackupVault.LooksEncrypted(Path.Combine(directory, "missing")));
    }

    // ---------------------------------------------------------------- damage is refused

    [Fact]
    public void A_flipped_byte_anywhere_in_the_body_is_refused()
    {
        var vault = NewVault();
        var encrypted = Encrypt(vault, Random(Chunk * 4 + 10));

        // Every kind of byte in the body: a flag, a length, the ciphertext, a tag.
        for (var position = HeaderSize; position < encrypted.Length; position += 7)
        {
            var damaged = (byte[])encrypted.Clone();
            damaged[position] ^= 0x01;

            Assert.Throws<BackupDecryptionException>(() => Decrypt(vault, damaged));
        }
    }

    [Fact]
    public void A_file_cut_short_is_refused_wherever_it_is_cut()
    {
        var vault = NewVault();
        var encrypted = Encrypt(vault, Random(Chunk * 3 + 10));

        // In the header, in the middle of a part, and exactly between parts (where the file looks tidy but has lost
        // its last part).
        foreach (var length in new[] { 20, HeaderSize + 10, HeaderSize + 1 + 4 + Chunk + 16, encrypted.Length - 1 })
        {
            Assert.Throws<BackupDecryptionException>(() => Decrypt(vault, encrypted[..length]));
        }
    }

    [Fact]
    public void Dropping_the_last_part_cleanly_is_noticed_because_the_last_part_is_marked()
    {
        var vault = NewVault();
        var encrypted = Encrypt(vault, Random(Chunk * 3)); // three full parts; the third is the marked last one

        var oneFrame = 1 + 4 + Chunk + 16;
        var withoutFinal = encrypted[..(HeaderSize + oneFrame * 2)];

        var e = Assert.Throws<BackupDecryptionException>(() => Decrypt(vault, withoutFinal));
        Assert.Contains("incomplete", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extra_bytes_after_the_end_are_refused()
    {
        var vault = NewVault();
        var encrypted = Encrypt(vault, Random(100)).Concat(new byte[] { 0 }).ToArray();

        var e = Assert.Throws<BackupDecryptionException>(() => Decrypt(vault, encrypted));
        Assert.Contains("extra data", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_parts_swapped_are_refused()
    {
        var vault = NewVault();
        var encrypted = Encrypt(vault, Random(Chunk * 4));
        var frame = 1 + 4 + Chunk + 16;

        var swapped = (byte[])encrypted.Clone();
        encrypted.AsSpan(HeaderSize + frame, frame).CopyTo(swapped.AsSpan(HeaderSize));
        encrypted.AsSpan(HeaderSize, frame).CopyTo(swapped.AsSpan(HeaderSize + frame));

        Assert.Throws<BackupDecryptionException>(() => Decrypt(vault, swapped));
    }

    [Fact]
    public void Parts_from_another_backup_cannot_be_spliced_in()
    {
        var vault = NewVault();
        var plain = Random(Chunk * 4);
        var a = Encrypt(vault, plain);
        var b = Encrypt(vault, plain);
        var frame = 1 + 4 + Chunk + 16;

        // Same plain text, same key, different backups: a part from one does not belong in the other.
        var spliced = (byte[])a.Clone();
        b.AsSpan(HeaderSize + frame, frame).CopyTo(spliced.AsSpan(HeaderSize + frame));

        Assert.Throws<BackupDecryptionException>(() => Decrypt(vault, spliced));
    }

    [Fact]
    public void A_file_that_is_not_one_of_these_is_refused_with_a_plain_message()
    {
        var vault = NewVault();

        foreach (var junk in new[] { Random(500), "PGDMP\u0001\u000e\u0000 this is a plain dump"u8.ToArray(), [], new byte[10] })
        {
            var e = Assert.Throws<BackupDecryptionException>(() => Decrypt(vault, junk));
            Assert.Contains("not an encrypted", e.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_newer_format_is_refused_for_what_it_is()
    {
        var vault = NewVault();
        var encrypted = Encrypt(vault, Random(10));
        encrypted[4] = 2;

        var e = Assert.Throws<BackupDecryptionException>(() => Decrypt(vault, encrypted));
        Assert.Contains("newer version", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- the keys

    [Fact]
    public void Another_machines_backup_asks_for_the_recovery_key_and_the_recovery_key_opens_it()
    {
        var made = NewVault();
        var plain = Random(300);
        var recoveryKey = made.CreateRecoveryKey(null).Key;
        var encrypted = Encrypt(made, plain);

        // A new machine, with a key of its own and none of the first machine's.
        var elsewhere = NewVault();

        var needs = Assert.Throws<BackupDecryptionException>(() => Decrypt(elsewhere, encrypted));
        Assert.True(needs.NeedsRecoveryKey);
        Assert.Contains("recovery key", needs.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(plain, Decrypt(elsewhere, encrypted, recoveryKey));
    }

    [Fact]
    public void Only_the_encrypting_machine_needs_no_recovery_key()
    {
        var vault = NewVault();
        var plain = Random(300);

        Assert.Equal(plain, Decrypt(vault, Encrypt(vault, plain)));
    }

    [Fact]
    public void A_wrong_recovery_key_is_refused_and_says_it_does_not_belong()
    {
        var made = NewVault();
        var encrypted = Encrypt(made, Random(100));
        made.CreateRecoveryKey(null);

        var other = NewVault();
        var wrong = other.CreateRecoveryKey(null).Key;

        var e = Assert.Throws<BackupDecryptionException>(() => Decrypt(NewVault(), encrypted, wrong));
        Assert.Contains("does not belong", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_damaged_machine_slot_still_opens_with_the_recovery_key_and_not_without_it()
    {
        var vault = NewVault();
        var plain = Random(200);
        var recoveryKey = vault.CreateRecoveryKey(null).Key;
        var encrypted = Encrypt(vault, plain);

        var damaged = (byte[])encrypted.Clone();
        damaged[41 + 20] ^= 0xFF; // inside the machine wrap

        var withoutKey = Assert.Throws<BackupDecryptionException>(() => Decrypt(vault, damaged));
        Assert.Contains("damaged", withoutKey.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(plain, Decrypt(vault, damaged, recoveryKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCD")]
    [InlineData("not a key at all")]
    [InlineData("0000-1111-8888-9999-0000-1111-8888-9999-0000-1111-8888-9999-0000")]
    public void Something_that_is_not_a_recovery_key_is_refused_with_help(string text)
    {
        var e = Assert.Throws<BackupDecryptionException>(() => BackupVault.ParseRecoveryKey(text));
        Assert.Contains("52", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_recovery_key_is_written_in_groups_and_read_back_ignoring_case_spaces_and_dashes()
    {
        var vault = NewVault();
        var key = vault.CreateRecoveryKey(null).Key;

        Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{4}){12}$", key);

        var bytes = BackupVault.ParseRecoveryKey(key);
        Assert.Equal(32, bytes.Length);
        Assert.Equal(bytes, BackupVault.ParseRecoveryKey(key.ToLowerInvariant().Replace("-", " ")));
        Assert.Equal(bytes, BackupVault.ParseRecoveryKey(" " + key.Replace("-", "") + " "));
        Assert.Equal(key, BackupVault.FormatRecoveryKey(bytes));
    }

    [Fact]
    public void A_recovery_key_typed_with_a_zero_for_an_O_a_one_for_an_I_or_an_eight_for_a_B_is_still_understood()
    {
        // A key written on paper and typed back on a bad day. Every key has such letters somewhere in 52 characters,
        // and a fixed one is used so the test always has them.
        var bytes = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray();
        var written = BackupVault.FormatRecoveryKey(bytes);
        Assert.Matches("[OIB]", written);

        var typed = written.Replace('O', '0').Replace('I', '1').Replace('B', '8');
        Assert.NotEqual(written, typed);

        Assert.Equal(bytes, BackupVault.ParseRecoveryKey(typed));
    }

    [Fact]
    public void A_new_recovery_key_replaces_the_old_one_for_every_backup_this_machine_can_open_without_touching_their_bodies()
    {
        var directory = NewDirectory();
        Directory.CreateDirectory(directory);
        var vault = NewVault();
        var plain1 = Random(300);
        var plain2 = Random(120);

        var oldKey = vault.CreateRecoveryKey(null).Key;
        File.WriteAllBytes(Path.Combine(directory, "hospitalpm-1.dump.enc"), Encrypt(vault, plain1));
        File.WriteAllBytes(Path.Combine(directory, "hospitalpm-2.dump.enc"), Encrypt(vault, plain2));

        // One made on another machine: this machine's key cannot open it, so it is left alone.
        var foreign = Encrypt(NewVault(), Random(50));
        File.WriteAllBytes(Path.Combine(directory, "hospitalpm-foreign.dump.enc"), foreign);

        var bodyBefore = File.ReadAllBytes(Path.Combine(directory, "hospitalpm-1.dump.enc"))[HeaderSize..];

        var result = vault.CreateRecoveryKey(directory);

        Assert.Equal(2, result.Rewrapped);
        Assert.Equal(1, result.Skipped);
        Assert.NotEqual(oldKey, result.Key);
        Assert.Equal(foreign, File.ReadAllBytes(Path.Combine(directory, "hospitalpm-foreign.dump.enc")));
        Assert.Equal(bodyBefore, File.ReadAllBytes(Path.Combine(directory, "hospitalpm-1.dump.enc"))[HeaderSize..]);

        // On another machine: the new key opens them, the old one no longer does.
        var elsewhere = NewVault();
        var one = File.ReadAllBytes(Path.Combine(directory, "hospitalpm-1.dump.enc"));
        var two = File.ReadAllBytes(Path.Combine(directory, "hospitalpm-2.dump.enc"));

        Assert.Equal(plain1, Decrypt(elsewhere, one, result.Key));
        Assert.Equal(plain2, Decrypt(elsewhere, two, result.Key));
        Assert.Throws<BackupDecryptionException>(() => Decrypt(elsewhere, one, oldKey));

        // And the machine itself never needed a key.
        Assert.Equal(plain1, Decrypt(vault, one));
    }

    [Fact]
    public void A_backup_made_after_a_new_recovery_key_opens_with_that_key()
    {
        var vault = NewVault();
        vault.CreateRecoveryKey(null);
        var key = vault.CreateRecoveryKey(null).Key;
        var plain = Random(200);

        var encrypted = Encrypt(vault, plain);

        Assert.Equal(plain, Decrypt(NewVault(), encrypted, key));
    }

    [Fact]
    public void The_status_says_what_has_been_done_and_never_a_key()
    {
        var vault = NewVault();

        var none = vault.Status();
        Assert.False(none.MasterKeyPresent);
        Assert.Null(none.RecoveryKeyCreatedAtUtc);
        Assert.False(none.RecoveryKeySaved);

        vault.EnsureKeys();
        var made = vault.Status();
        Assert.True(made.MasterKeyPresent);
        Assert.NotNull(made.RecoveryKeyCreatedAtUtc);
        Assert.False(made.RecoveryKeySaved);

        vault.MarkRecoveryKeySaved();
        Assert.True(vault.Status().RecoveryKeySaved);

        // A new recovery key has not been written down yet.
        vault.CreateRecoveryKey(null);
        Assert.False(vault.Status().RecoveryKeySaved);
    }

    [Fact]
    public void The_keys_survive_a_restart_and_the_key_files_hold_no_readable_recovery_key()
    {
        var directory = NewDirectory();
        var first = new BackupVault(directory, chunkSize: Chunk);
        var plain = Random(300);
        var encrypted = Encrypt(first, plain);
        var recoveryKey = first.CreateRecoveryKey(null).Key;

        // The same folder, a new object: what a restart of the service is.
        var second = new BackupVault(directory, chunkSize: Chunk);
        Assert.Equal(plain, Decrypt(second, encrypted));

        // The recovery key is on the machine only wrapped under the machine key.
        var raw = BackupVault.ParseRecoveryKey(recoveryKey);
        foreach (var file in Directory.GetFiles(directory))
        {
            Assert.False(File.ReadAllBytes(file).AsSpan().IndexOf(raw) >= 0, $"the recovery key is readable in {Path.GetFileName(file)}");
        }

        if (!OperatingSystem.IsWindows())
        {
            foreach (var file in Directory.GetFiles(directory))
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            }
        }
    }

    [Fact]
    public void A_damaged_machine_key_is_never_quietly_replaced()
    {
        var directory = NewDirectory();
        var vault = new BackupVault(directory, chunkSize: Chunk);
        vault.EnsureKeys();

        var keyFile = Path.Combine(directory, "backup-master.key");
        File.WriteAllBytes(keyFile, new byte[10]);

        // A new key would lock this machine out of every backup it has made.
        var e = Assert.Throws<InvalidOperationException>(() => vault.EnsureKeys());
        Assert.Contains("damaged", e.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, File.ReadAllBytes(keyFile).Length);
    }

    // ---------------------------------------------------------------- files

    [Fact]
    public async Task A_file_is_encrypted_and_decrypted_through_a_temporary_name_and_leaves_nothing_behind()
    {
        var directory = NewDirectory();
        Directory.CreateDirectory(directory);
        var vault = NewVault();
        var plain = Random(5000);

        var source = Path.Combine(directory, "a.dump");
        var encrypted = Path.Combine(directory, "a.dump.enc");
        var restored = Path.Combine(directory, "restored.dump");
        await File.WriteAllBytesAsync(source, plain);

        await vault.EncryptFileAsync(source, encrypted);
        await vault.DecryptFileAsync(encrypted, restored);

        Assert.Equal(plain, await File.ReadAllBytesAsync(restored));
        Assert.True(BackupVault.LooksEncrypted(encrypted));
        Assert.Empty(Directory.GetFiles(directory, "*.partial"));
    }

    [Fact]
    public async Task A_failed_decryption_writes_no_output_file()
    {
        var directory = NewDirectory();
        Directory.CreateDirectory(directory);
        var vault = NewVault();

        var encrypted = Path.Combine(directory, "a.dump.enc");
        await File.WriteAllBytesAsync(encrypted, Encrypt(vault, Random(500)));

        var output = Path.Combine(directory, "out.dump");
        await Assert.ThrowsAsync<BackupDecryptionException>(() => NewVault().DecryptFileAsync(encrypted, output));

        // Neither the real name nor a half-written one: a restore must never be handed part of a database.
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(directory, "*.partial"));
    }
}
