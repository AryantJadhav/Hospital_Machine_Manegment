using System.Globalization;
using System.Text.Json;
using HospitalPm.Domain.Licensing;
using Microsoft.Extensions.Options;

namespace HospitalPm.Infrastructure.Licensing;

public sealed class LicenceOptions
{
    public const string Section = "Licence";

    /// <summary>
    /// Our signing key's public half, base64 SubjectPublicKeyInfo, produced by
    /// the vendor licence tool's keygen command. Empty in source: a release
    /// build sets it, and the private half never enters the repository.
    /// </summary>
    public string PublicKey { get; set; } = string.Empty;

    /// <summary>
    /// Licence file location. Relative paths resolve against the binary's own
    /// directory rather than the working directory, which for a Windows
    /// Service is system32.
    /// </summary>
    public string Path { get; set; } = "hospitalpm.licence";

    /// <summary>
    /// Days after a licence's end date before the software turns read-only.
    /// </summary>
    public int GraceDays { get; set; } = LicenceVerifier.DefaultGraceDays;
}

/// <summary>
/// The installation's licence, read from disk and checked offline.
///
/// Two things are remembered beside the licence file, in a small JSON file that
/// the licence never depends on for its signature:
///
///  - When each duration-based licence first began. A key that runs for "12
///    weeks" has to start counting somewhere, and pasting the same key in again
///    later must not start the clock over.
///  - The latest date the software has seen. Putting the machine's clock back
///    does not bring an expired licence back to life, because the later of the
///    two dates is used.
///
/// Neither is tamper-proof against someone with administrator rights on the
/// machine, and is not meant to be: a licence is a reminder to renew, not a
/// defence against a determined thief. It stops the casual cases.
/// </summary>
public sealed class LicenceService(IOptions<LicenceOptions> options, TimeProvider clock)
{
    private readonly LicenceOptions _options = options.Value;
    private readonly object _gate = new();

    // The verdict for a given file on a given day only changes when the file or
    // the day does, and every write request asks for it.
    private ((string? Text, DateOnly Today) Key, LicenceStatus Status)? _cached;

    public string ResolvePath() =>
        System.IO.Path.IsPathRooted(_options.Path)
            ? _options.Path
            : System.IO.Path.Combine(AppContext.BaseDirectory, _options.Path);

    private string StatePath => ResolvePath() + ".state";

    public LicenceStatus Current()
    {
        if (string.IsNullOrWhiteSpace(_options.PublicKey))
        {
            // A build with no public key cannot validate anything. Saying so
            // beats reporting every licence as forged.
            return new LicenceStatus(LicenceState.Missing, null,
                "This build has no licence key configured, so licensing is not enforced.");
        }

        lock (_gate)
        {
            var path = ResolvePath();
            var today = EffectiveToday();

            string? text = null;
            if (File.Exists(path))
            {
                try
                {
                    text = File.ReadAllText(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return new LicenceStatus(LicenceState.Invalid, null,
                        "The licence file could not be read from disk. "
                        + "Check the service account's permissions on the installation folder.");
                }
            }

            // The file is a few hundred bytes, so it is read every time and the
            // signature check, which is the costly part, is what is remembered.
            // A replaced file is noticed immediately because its text differs.
            var key = (text, today);

            if (_cached is { } hit && hit.Key == key)
            {
                return hit.Status;
            }

            var status = Assess(text, today, record: true);
            _cached = (key, status);
            return status;
        }
    }

    /// <summary>
    /// Installs a licence file, but only if it verifies.
    ///
    /// Refusing to save a bad licence means the installed file is always one
    /// that worked at least once, so a hospital cannot overwrite a good
    /// licence with a truncated email attachment and lose both.
    /// </summary>
    public (bool Saved, LicenceStatus Status) Install(string fileText)
    {
        if (string.IsNullOrWhiteSpace(_options.PublicKey))
        {
            return (false, new LicenceStatus(LicenceState.Missing, null,
                "This build has no licence key configured, so a licence cannot be installed."));
        }

        lock (_gate)
        {
            var today = EffectiveToday();
            var status = Assess(fileText, today, record: false);

            // Expired and read-only licences are still installable: a hospital
            // renewing after a lapse should be able to put the old file back
            // while the new one is being issued, and it is honest about what it is.
            if (status.State is LicenceState.Invalid or LicenceState.Missing)
            {
                return (false, status);
            }

            try
            {
                var path = ResolvePath();
                var directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, fileText);
                _cached = null;

                // Only now that it is saved does a duration-based licence begin.
                status = Assess(fileText, today, record: true);
                return (true, status);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return (false, new LicenceStatus(LicenceState.Invalid, null,
                    "The licence is valid but could not be saved. "
                    + "Check the service account's permissions on the installation folder."));
            }
        }
    }

    private LicenceStatus Assess(string? text, DateOnly today, bool record)
    {
        var verifier = new LicenceVerifier(_options.PublicKey);

        DateOnly? activatedOn = null;
        if (verifier.Trusted(text) is { DurationDays: not null } licence)
        {
            var state = ReadState();
            var key = licence.LicenceId.ToString("D", CultureInfo.InvariantCulture);

            if (state.Activations.TryGetValue(key, out var raw)
                && DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var began))
            {
                activatedOn = began;
            }
            else if (record)
            {
                activatedOn = today;
                state.Activations[key] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                WriteState(state);
            }
        }

        return verifier.Verify(text, today, activatedOn, Math.Max(0, _options.GraceDays));
    }

    /// <summary>The later of the machine's date and the latest date ever seen.</summary>
    private DateOnly EffectiveToday()
    {
        var real = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var state = ReadState();

        if (state.LastSeen is { } raw
            && DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var seen)
            && seen > real)
        {
            return seen;
        }

        if (state.LastSeen != real.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
        {
            state.LastSeen = real.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            WriteState(state);
        }

        return real;
    }

    private sealed class RememberedState
    {
        public Dictionary<string, string> Activations { get; set; } = [];

        public string? LastSeen { get; set; }
    }

    private RememberedState ReadState()
    {
        try
        {
            var path = StatePath;
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<RememberedState>(File.ReadAllText(path)) ?? new RememberedState();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable is treated as empty: it is a convenience beside the
            // licence, and must never be the reason a hospital cannot log in.
        }

        return new RememberedState();
    }

    private void WriteState(RememberedState state)
    {
        try
        {
            var path = StatePath;
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(state));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // As above: not being able to remember only means the next check
            // starts from what the clock says.
        }
    }
}
