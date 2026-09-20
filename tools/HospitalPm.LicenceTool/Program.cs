using System.Globalization;
using System.Security.Cryptography;
using HospitalPm.Domain.Licensing;

// Vendor-side licence tool. Never shipped to a hospital.
//
// Two jobs: make a signing keypair once, and sign a licence per customer.
// The private key stays on the machine that runs this and never enters the
// repository — the public half is the only part that goes into the product.

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
        Hospital PM licence tool (internal)

          keygen  --out <dir>
              Writes signing-key.pem (keep secret) and public-key.txt.
              The contents of public-key.txt go into the product's
              Licence:PublicKey setting.

          sign    --key <signing-key.pem> --hospital "<name>" --out <file.licence>
                  [--expires yyyy-MM-dd | --days <n>] [--modules a,b,c]
                  [--max-equipment <n>] [--notes "<text>"]
              --expires ends the licence on a calendar date. --days runs it
              for that many days from the day it is first installed, so a pilot
              key can be issued before the install date is known.
              Omit both for a perpetual licence.

          verify  --public-key <base64|file> --file <file.licence>
              Checks a licence the way the product will.
        """);
    return 1;
}

static int KeyGen(string[] args)
{
    var outDir = Arg(args, "--out") ?? ".";
    Directory.CreateDirectory(outDir);

    var keyPath = Path.Combine(outDir, "signing-key.pem");
    if (File.Exists(keyPath))
    {
        // Overwriting the signing key would invalidate every licence ever
        // issued, with no way to get it back.
        Console.Error.WriteLine($"{keyPath} already exists. Refusing to overwrite a signing key.");
        return 1;
    }

    using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    File.WriteAllText(keyPath, ecdsa.ExportPkcs8PrivateKeyPem());
    var publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    File.WriteAllText(Path.Combine(outDir, "public-key.txt"), publicKey);

    Console.WriteLine($"Private key : {keyPath}   <-- keep secret, never commit");
    Console.WriteLine($"Public key  : {Path.Combine(outDir, "public-key.txt")}");
    Console.WriteLine();
    Console.WriteLine(publicKey);
    return 0;
}

static int Sign(string[] args)
{
    var keyPath = Arg(args, "--key");
    var hospital = Arg(args, "--hospital");
    var outPath = Arg(args, "--out");

    if (keyPath is null || hospital is null || outPath is null)
    {
        Console.Error.WriteLine("--key, --hospital and --out are required.");
        return Usage();
    }

    if (!File.Exists(keyPath))
    {
        Console.Error.WriteLine($"No signing key at {keyPath}.");
        return 1;
    }

    DateOnly? expires = null;
    if (Arg(args, "--expires") is { } raw)
    {
        if (!DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            Console.Error.WriteLine($"--expires must be yyyy-MM-dd, got '{raw}'.");
            return 1;
        }
        expires = parsed;
    }

    int? durationDays = null;
    if (Arg(args, "--days") is { } daysRaw)
    {
        if (expires is not null)
        {
            Console.Error.WriteLine("Use --expires or --days, not both.");
            return 1;
        }

        if (!int.TryParse(daysRaw, CultureInfo.InvariantCulture, out var d) || d <= 0)
        {
            Console.Error.WriteLine($"--days must be a positive number, got '{daysRaw}'.");
            return 1;
        }
        durationDays = d;
    }

    int? maxEquipment = null;
    if (Arg(args, "--max-equipment") is { } cap)
    {
        if (!int.TryParse(cap, CultureInfo.InvariantCulture, out var n) || n <= 0)
        {
            Console.Error.WriteLine($"--max-equipment must be a positive number, got '{cap}'.");
            return 1;
        }
        maxEquipment = n;
    }

    var modules = (Arg(args, "--modules") ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    var licence = new Licence(
        LicenceId: Guid.NewGuid(),
        HospitalName: hospital,
        IssuedOn: DateOnly.FromDateTime(DateTime.UtcNow),
        ExpiresOn: expires,
        Modules: modules,
        MaxEquipment: maxEquipment,
        Notes: Arg(args, "--notes"),
        DurationDays: durationDays);

    var payload = LicenceFile.Serialise(licence);

    using var ecdsa = ECDsa.Create();
    ecdsa.ImportFromPem(File.ReadAllText(keyPath));

    var signature = ecdsa.SignData(
        payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    File.WriteAllText(outPath, LicenceFile.Format(payload, signature));

    Console.WriteLine($"Licence  : {outPath}");
    Console.WriteLine($"Id       : {licence.LicenceId}");
    Console.WriteLine($"Hospital : {licence.HospitalName}");
    Console.WriteLine($"Expires  : {(durationDays is { } n2 ? $"{n2} days after it is first installed" : expires is null ? "never" : expires.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture))}");
    Console.WriteLine($"Modules  : {(modules.Count == 0 ? "core only" : string.Join(", ", modules))}");
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
        Console.Error.WriteLine($"No licence file at {filePath}.");
        return 1;
    }

    var status = new LicenceVerifier(publicKey)
        .Verify(File.ReadAllText(filePath), DateOnly.FromDateTime(DateTime.UtcNow));

    Console.WriteLine($"{status.State}: {status.Message}");
    return status.State == LicenceState.Valid ? 0 : 1;
}

static string? Arg(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
