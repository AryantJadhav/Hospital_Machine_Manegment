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

    /// <summary>
    /// Where the Developer's own copy finds the licence signing key (a PEM file), to issue licences and lock codes.
    /// Empty everywhere else: a hospital's installation never has the key, so it has no way to make a licence, and
    /// the Developer's licence section says so instead of working.
    /// </summary>
    public string SigningKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// A second place the lock is remembered, in a different folder from the licence (the data folder). A lock
    /// that lives in one file is undone by deleting that file; with two, both have to go.
    /// </summary>
    public string LockMirrorDirectory { get; set; } = string.Empty;
}

/// <summary>
/// Whether the installation is locked, and by which licence's code. A lock turns everyone away until an unlock
/// code for the same licence, with a higher number, is entered.
/// </summary>
/// <param name="LicenceId">The licence the last accepted code named.</param>
/// <param name="Sequence">The number on that code. Codes at or below it are never accepted again.</param>
/// <param name="HospitalName">For the lock screen: who this installation is, so they can say so on the phone.</param>
public sealed record LockState(Guid LicenceId, long Sequence, bool Locked, string HospitalName, DateTime ChangedAtUtc);

/// <summary>What happened when a code was entered.</summary>
public sealed record CodeResult(bool Applied, string Message, LicenceCommand? Command = null);

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

    // ------------------------------------------------------------------ the equipment limit

    /// <summary>
    /// The most machines this installation may record, or null for no limit (no licence, a perpetual one with no
    /// cap, or a build that cannot check). Read-only is not asked here: it already refuses every new record.
    /// </summary>
    public int? EquipmentLimit()
    {
        var status = Current();
        return status.State is LicenceState.Valid or LicenceState.Expired ? status.Licence?.MaxEquipment : null;
    }

    // ------------------------------------------------------------------ the lock

    private (long Ticks, LockState? State)? _lockCache;

    /// <summary>A lock is read often (every request asks), so the answer is kept for a moment. Entering a code clears it.</summary>
    private const long LockCacheMilliseconds = 2000;

    private string LockPath => ResolvePath() + ".lock";

    private string? MirrorPath => string.IsNullOrWhiteSpace(_options.LockMirrorDirectory)
        ? null
        : System.IO.Path.Combine(_options.LockMirrorDirectory, "licence-lock.json");

    /// <summary>The lock on this installation, or null when it is not locked.</summary>
    public LockState? CurrentLock()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (_lockCache is not { } hit || now - hit.Ticks >= LockCacheMilliseconds)
            {
                hit = (now, ReadLockState());
                _lockCache = hit;
            }

            return hit.State is { Locked: true } ? hit.State : null;
        }
    }

    /// <summary>
    /// Checks a pasted lock or unlock code and, if it is ours, for this licence and newer than the last one
    /// accepted, applies it. A lock names the licence installed here. An unlock names the licence that locked it,
    /// so swapping the licence file for another does not slip past. Each licence's codes are numbered, and only a
    /// higher number is ever accepted, so an old unlock cannot undo a newer lock, nor the other way round.
    /// </summary>
    public CodeResult ApplyCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(_options.PublicKey))
        {
            return new CodeResult(false, "This build has no licence key configured, so a code cannot be checked.");
        }

        var command = LicenceCommandFile.Verify(code, _options.PublicKey);
        if (command is null)
        {
            return new CodeResult(false,
                "That code is not valid for this software. Check that all of it was copied, from the first line to the last.");
        }

        lock (_gate)
        {
            var current = ReadLockState();
            var installed = InstalledLicence();
            var locking = command.Action == LicenceAction.Lock;

            var target = locking
                ? installed?.LicenceId
                : current is { Locked: true } ? current.LicenceId : installed?.LicenceId;

            if (target != command.LicenceId)
            {
                return new CodeResult(false, "That code is for a different licence from the one on this installation.");
            }

            var last = current?.LicenceId == command.LicenceId ? current.Sequence : 0;
            if (command.Sequence <= last)
            {
                return new CodeResult(false, "That code has already been used, or a newer one has replaced it.");
            }

            var state = new LockState(
                command.LicenceId, command.Sequence, locking,
                installed?.HospitalName ?? current?.HospitalName ?? string.Empty,
                clock.GetUtcNow().UtcDateTime);

            if (!WriteLockState(state))
            {
                return new CodeResult(false,
                    "The code is valid but could not be saved. Check the service account's permissions on the installation folder.");
            }

            _lockCache = null;
            return new CodeResult(true,
                locking
                    ? "This installation is now locked. Nobody can sign in until an unlock code is entered."
                    : "This installation is unlocked.",
                command);
        }
    }

    private Licence? InstalledLicence()
    {
        try
        {
            var path = ResolvePath();
            return File.Exists(path) ? new LicenceVerifier(_options.PublicKey).Trusted(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// What the two places say. Per licence the highest number wins; a lock stands if any licence's latest word is
    /// a lock. If the places disagree, or one has gone, both are made to say what the winner says.
    /// </summary>
    private LockState? ReadLockState()
    {
        var paths = new List<string> { LockPath };
        if (MirrorPath is { } mirror)
        {
            paths.Add(mirror);
        }

        var found = paths.Select(ReadLockFile).OfType<LockState>().ToList();
        if (found.Count == 0)
        {
            return null;
        }

        var latest = found
            .GroupBy(s => s.LicenceId)
            .Select(g => g.OrderByDescending(s => s.Sequence).ThenByDescending(s => s.Locked).First())
            .ToList();
        var winner = latest.FirstOrDefault(s => s.Locked) ?? latest.OrderByDescending(s => s.ChangedAtUtc).First();

        if (found.Count < paths.Count || found.Any(s => s != winner))
        {
            WriteLockState(winner);
        }

        return winner;
    }

    private static LockState? ReadLockFile(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<LockState>(File.ReadAllText(path), LicenceFile.Json)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Unreadable is treated as absent, and the other place is asked.
            return null;
        }
    }

    private bool WriteLockState(LockState state)
    {
        var written = false;
        foreach (var path in new[] { LockPath, MirrorPath }.OfType<string>())
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, JsonSerializer.Serialize(state, LicenceFile.Json));
                written = true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // One place failing is not the end of it: the other still holds the lock.
            }
        }

        return written;
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
