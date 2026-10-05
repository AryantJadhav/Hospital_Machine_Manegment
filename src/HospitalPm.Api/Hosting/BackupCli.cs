using HospitalPm.Infrastructure.Operations;

namespace HospitalPm.Api.Hosting;

/// <summary>
/// <c>hospitalpm backup-decrypt</c>: opens an encrypted backup without the application running.
///
/// For the day the machine is gone. A backup is encrypted, so a new server cannot restore it by
/// itself; with this and the recovery key an administrator turns the file back into an ordinary
/// PostgreSQL dump and restores it with pg_restore. It is part of the same program on purpose, so the
/// one thing a hospital must have to hand is the program they already installed.
///
///   hospitalpm backup-decrypt FILE.dump.enc OUT.dump [--recovery-key KEY] [--key-dir DIR] [--force]
///
/// Without a recovery key it uses the key on this machine, which only works for backups this machine
/// made. The output is the plain dump: it holds the whole database, so it goes somewhere private and is
/// deleted when the restore is done.
/// </summary>
public static class BackupCli
{
    public static int Run(string[] args)
    {
        string? input = null, output = null, recoveryKey = null, keyDirectory = null;
        var force = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--recovery-key" when i + 1 < args.Length:
                    recoveryKey = args[++i];
                    break;
                case "--key-dir" when i + 1 < args.Length:
                    keyDirectory = args[++i];
                    break;
                case "--force":
                    force = true;
                    break;
                case "--help" or "-h" or "/?":
                    return Usage(0);
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        return Usage(2, $"Unknown option {args[i]}.");
                    }

                    if (input is null) input = args[i];
                    else if (output is null) output = args[i];
                    else return Usage(2, "Too many file names.");
                    break;
            }
        }

        if (input is null || output is null)
        {
            return Usage(2);
        }

        if (!File.Exists(input))
        {
            return Fail($"There is no file at {input}.");
        }

        if (File.Exists(output) && !force)
        {
            return Fail($"{output} already exists. Choose another name, or add --force to replace it.");
        }

        if (!BackupVault.LooksEncrypted(input))
        {
            return Fail("That file is not an encrypted Hospital PM backup. If it is an older backup it is already a plain dump: restore it with pg_restore directly.");
        }

        var vault = new BackupVault(keyDirectory ?? Path.Combine(InstallPaths.DataDirectory(), "keys"));

        try
        {
            vault.DecryptFileAsync(input, output, recoveryKey).GetAwaiter().GetResult();
        }
        catch (BackupDecryptionException e)
        {
            return Fail(e.NeedsRecoveryKey && recoveryKey is null
                ? "This machine's key does not open this backup: it was made on another machine, or the key here has been replaced. "
                  + "Run the command again with --recovery-key followed by the recovery key written down for this installation."
                : e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Fail($"Could not read or write the files: {e.Message}");
        }

        Console.WriteLine($"Decrypted {input}");
        Console.WriteLine($"     to {output}");
        Console.WriteLine();
        Console.WriteLine("That is a plain PostgreSQL dump of the whole database. Restore it with pg_restore, then delete it.");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static int Usage(int code, string? problem = null)
    {
        var writer = code == 0 ? Console.Out : Console.Error;
        if (problem is not null)
        {
            writer.WriteLine(problem);
            writer.WriteLine();
        }

        writer.WriteLine("Usage: hospitalpm backup-decrypt <backup.dump.enc> <output.dump> [options]");
        writer.WriteLine();
        writer.WriteLine("  --recovery-key KEY   the recovery key written down for this installation");
        writer.WriteLine("                       (needed for a backup made on another machine)");
        writer.WriteLine("  --key-dir DIR        where this machine's backup key is, if not the usual place");
        writer.WriteLine("  --force              replace the output file if it exists");
        return code;
    }
}
