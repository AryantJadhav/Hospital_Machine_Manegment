using System.Globalization;
using System.Security.Cryptography;
using HospitalPm.Domain.Updates;

// Vendor-side update tool. Never shipped to a hospital.
//
// Two jobs: make an update signing keypair once, and sign one release. The
// private key stays on the machine that runs this and never enters the
// repository — the public half goes into the product's Update:PublicKey.
//
// This is a different key from the licence one on purpose. A licence
// signature says a hospital may use software it already has. This signature
// says an executable may run on their machine as LocalSystem. Anyone who can
// issue licences should not thereby be able to push code to every install.

return args.FirstOrDefault() switch
{
    "keygen" => KeyGen(args),
    "sign" => Sign(args),
    "verify" => VerifyFile(args),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("""
        Hospital PM update tool (internal)

          keygen  --out <dir>
              Writes update-signing-key.pem (keep secret) and
              update-public-key.txt. The contents of update-public-key.txt go
              into the UPDATE_PUBLIC_KEY repository secret, which the release
              workflow bakes into the build as Update:PublicKey.

          sign    --key <update-signing-key.pem>
                  --installer <HospitalPM-Setup-x.y.z.exe>
                  [--version x.y.z] [--notes "<text>"] [--out <file.update>]
              Hashes the installer and signs a manifest naming it. The .update
              file is written next to the installer unless --out says
              otherwise; the two must travel together.

          verify  --public-key <base64|file> --file <file.update>
                  [--running x.y.z]
              Checks an update file the way the product will, including the
              installer's hash. Run this on the copy that is about to be sent.
        """);
    return 1;
}

static int KeyGen(string[] args)
{
    var outDir = Arg(args, "--out") ?? ".";
    Directory.CreateDirectory(outDir);

    var keyPath = Path.Combine(outDir, "update-signing-key.pem");
    if (File.Exists(keyPath))
    {
        // Overwriting this key means no installed copy of the software can
        // ever verify an update again, on any machine, with no way back.
        Console.Error.WriteLine($"{keyPath} already exists. Refusing to overwrite an update signing key.");
        return 1;
    }

    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    File.WriteAllText(keyPath, ecdsa.ExportPkcs8PrivateKeyPem());
    var publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    File.WriteAllText(Path.Combine(outDir, "update-public-key.txt"), publicKey);

    Console.WriteLine($"Private key : {keyPath}   <-- keep secret, never commit, back up off this machine");
    Console.WriteLine($"Public key  : {Path.Combine(outDir, "update-public-key.txt")}");
    Console.WriteLine();
    Console.WriteLine(publicKey);
    Console.WriteLine();
    Console.WriteLine("Put the public key in the UPDATE_PUBLIC_KEY repository secret before the next release.");
    Console.WriteLine("A build made without it cannot install updates at all - which is the safe failure,");
    Console.WriteLine("but it is permanent for every machine that installs that build.");
    return 0;
}

static int Sign(string[] args)
{
    var keyPath = Arg(args, "--key");
    var installerPath = Arg(args, "--installer");

    if (keyPath is null || installerPath is null)
    {
        Console.Error.WriteLine("--key and --installer are required.");
        return Usage();
    }

    if (!File.Exists(keyPath))
    {
        Console.Error.WriteLine($"No update signing key at {keyPath}.");
        return 1;
    }

    if (!File.Exists(installerPath))
    {
        Console.Error.WriteLine($"No installer at {installerPath}.");
        return 1;
    }

    var installer = new FileInfo(installerPath);

    // The version can be read off the installer's own file name, which is how
    // the release workflow names it. Typing it separately is how a manifest
    // ends up claiming 1.2.0 for a 1.1.0 installer.
    var version = Arg(args, "--version") ?? VersionFromFileName(installer.Name);
    if (version is null)
    {
        Console.Error.WriteLine(
            $"Could not read a version out of '{installer.Name}'. Pass --version x.y.z.");
        return 1;
    }

    if (!System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+$"))
    {
        Console.Error.WriteLine($"--version must be x.y.z, got '{version}'.");
        return 1;
    }

    Console.WriteLine($"Hashing {installer.Name} ({Megabytes(installer.Length)})...");
    string sha256;
    using (var stream = installer.OpenRead())
    {
        sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    var manifest = new UpdateManifest(
        Version: version,
        InstallerFileName: installer.Name,
        Sha256: sha256,
        SizeBytes: installer.Length,
        ReleasedOn: DateOnly.FromDateTime(DateTime.UtcNow),
        Notes: Arg(args, "--notes"));

    var payload = UpdateManifestFile.Serialise(manifest);

    using var ecdsa = ECDsa.Create();
    ecdsa.ImportFromPem(File.ReadAllText(keyPath));

    var signature = ecdsa.SignData(
        payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    // Next to the installer by default. The two files have to be copied
    // across together and a default that separates them invites the one
    // mistake this design cannot recover from.
    var outPath = Arg(args, "--out")
        ?? Path.Combine(
            installer.DirectoryName ?? ".",
            $"HospitalPM-{version}.update");

    File.WriteAllText(outPath, UpdateManifestFile.Format(payload, signature));

    Console.WriteLine();
    Console.WriteLine($"Update file : {outPath}");
    Console.WriteLine($"Version     : {version}");
    Console.WriteLine($"Installer   : {installer.Name}");
    Console.WriteLine($"SHA-256     : {sha256}");
    Console.WriteLine($"Size        : {installer.Length:N0} bytes");
    Console.WriteLine();
    Console.WriteLine("Send both files. The hospital copies them into the same folder.");
    return 0;
}

static int VerifyFile(string[] args)
{
    var keyArg = Arg(args, "--public-key");
    var filePath = Arg(args, "--file");

    if (keyArg is null || filePath is null)
    {
        Console.Error.WriteLine("--public-key and --file are required.");
        return Usage();
    }

    // Accept the key inline or as a path, because both are natural to type.
    var publicKey = File.Exists(keyArg) ? File.ReadAllText(keyArg).Trim() : keyArg;

    if (!File.Exists(filePath))
    {
        Console.Error.WriteLine($"No update file at {filePath}.");
        return 1;
    }

    // Defaults to 0.0.0 so a freshly signed file verifies as installable
    // rather than as "already installed" against whatever this machine runs.
    var running = Version.TryParse(Arg(args, "--running") ?? "0.0.0", out var v) ? v : new Version(0, 0, 0);

    var folder = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? ".";

    var candidate = new UpdateVerifier(publicKey).Verify(
        filePath, File.ReadAllText(filePath), folder, running, new DiskFiles());

    Console.WriteLine($"{candidate.State}: {candidate.Message}");
    return candidate.CanInstall ? 0 : 1;
}

static string? VersionFromFileName(string name)
{
    var match = System.Text.RegularExpressions.Regex.Match(name, @"(\d+\.\d+\.\d+)");
    return match.Success ? match.Groups[1].Value : null;
}

static string Megabytes(long bytes) =>
    (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";

static string? Arg(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

/// <summary>
/// The same disk access the product uses, kept here rather than referenced so
/// this tool depends only on the domain. Verifying with a different reader
/// than the product uses would be checking the wrong thing.
/// </summary>
internal sealed class DiskFiles : IUpdateFileSystem
{
    public string Combine(string folder, string fileName) => Path.Combine(folder, fileName);

    public bool FileExists(string path) => File.Exists(path);

    public long FileSize(string path) => new FileInfo(path).Length;

    public string Sha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
