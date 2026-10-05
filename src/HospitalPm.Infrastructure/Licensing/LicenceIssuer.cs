using System.Globalization;
using System.Security.Cryptography;
using HospitalPm.Domain.Licensing;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HospitalPm.Infrastructure.Licensing;

/// <summary>What the Developer asks for when issuing a licence.</summary>
/// <param name="HospitalName">Shown in the app and on every printed report.</param>
/// <param name="ExpiresOn">A calendar date the licence ends on. Not together with <paramref name="DurationDays"/>.</param>
/// <param name="DurationDays">Runs this many days from the day it is first installed. Neither: perpetual.</param>
/// <param name="MaxEquipment">The most machines it allows, or null for no cap.</param>
/// <param name="Modules">Module keys, lower case letters, digits and dashes.</param>
public sealed record IssueLicenceRequest(
    string? HospitalName,
    DateOnly? ExpiresOn,
    int? DurationDays,
    int? MaxEquipment,
    IReadOnlyList<string>? Modules,
    string? Notes);

/// <summary>Why a licence or code was not made, in words for the Developer.</summary>
public sealed class IssueException(string message) : Exception(message);

/// <summary>
/// Makes licences and lock codes. Only works on the copy of the software that has the signing key.
///
/// The key is read from its file for each licence made and let go straight after: it is not kept in memory, not
/// logged, and not in the database. A hospital's installation has no key, so it cannot issue anything, and says
/// so, rather than failing in some other way.
/// </summary>
public sealed class LicenceIssuer(
    HospitalPmDbContext db,
    IOptions<LicenceOptions> options,
    TimeProvider clock)
{
    private readonly LicenceOptions _options = options.Value;

    /// <summary>Whether this copy can sign, and if not, the reason in words for the Developer.</summary>
    public (bool Available, string? Problem) Availability()
    {
        if (string.IsNullOrWhiteSpace(_options.SigningKeyPath))
        {
            return (false, "This copy of Hospital PM has no signing key, so it cannot issue licences or codes. "
                           + "Run this on your own machine, with Licence:SigningKeyPath pointing at the signing key.");
        }

        if (!File.Exists(_options.SigningKeyPath))
        {
            return (false, $"There is no signing key at {_options.SigningKeyPath}.");
        }

        try
        {
            using var key = LoadKey();
            return (true, null);
        }
        catch (IssueException e)
        {
            return (false, e.Message);
        }
    }

    public async Task<IssuedLicence> IssueAsync(IssueLicenceRequest request, int userId, CancellationToken ct = default)
    {
        var name = (request.HospitalName ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > 200)
        {
            throw new IssueException("Give the hospital's name, up to 200 characters.");
        }

        if (request.ExpiresOn is not null && request.DurationDays is not null)
        {
            throw new IssueException("Choose an end date, or a number of days from installation, not both.");
        }

        if (request.DurationDays is { } days and (<= 0 or > 3650))
        {
            throw new IssueException("The number of days must be between 1 and 3650.");
        }

        if (request.MaxEquipment is <= 0)
        {
            throw new IssueException("The most machines must be a positive number, or left empty for no cap.");
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (request.ExpiresOn is { } end && end < today)
        {
            throw new IssueException("The end date has already passed.");
        }

        var modules = (request.Modules ?? [])
            .Select(m => m.Trim().ToLowerInvariant())
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (modules.Any(m => m.Length > 40 || !m.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')))
        {
            throw new IssueException("A module is lower case letters, digits and dashes, up to 40 characters.");
        }

        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        if (notes is { Length: > 500 })
        {
            throw new IssueException("The notes are too long: 500 characters at most.");
        }

        var licence = new Licence(
            LicenceId: Guid.NewGuid(),
            HospitalName: name,
            IssuedOn: today,
            ExpiresOn: request.ExpiresOn,
            Modules: modules,
            MaxEquipment: request.MaxEquipment,
            Notes: notes,
            DurationDays: request.DurationDays);

        var payload = LicenceFile.Serialise(licence);
        string text;
        using (var key = LoadKey())
        {
            text = LicenceFile.Format(payload, key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        }

        var row = new IssuedLicence
        {
            LicenceId = licence.LicenceId,
            HospitalName = name,
            IssuedOn = today,
            ExpiresOn = request.ExpiresOn,
            DurationDays = request.DurationDays,
            MaxEquipment = request.MaxEquipment,
            Modules = string.Join(',', modules),
            Notes = notes,
            LicenceText = text,
            IssuedByUserId = userId,
            IssuedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.IssuedLicences.Add(row);
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>
    /// Makes the next lock or unlock code for a licence. The counter goes up each time, and is saved before the code
    /// is handed out, so a code is never made twice and never goes backwards.
    /// </summary>
    public async Task<(string Code, IssuedLicence Licence)> CommandAsync(int id, string action, CancellationToken ct = default)
    {
        var row = await db.IssuedLicences.SingleOrDefaultAsync(l => l.Id == id, ct)
                  ?? throw new KeyNotFoundException("No licence with that id.");

        var sequence = row.LockSequence + 1;
        string code;
        using (var key = LoadKey())
        {
            code = LicenceCommandFile.Sign(
                new LicenceCommand(row.LicenceId, action, sequence, clock.GetUtcNow().UtcDateTime), key);
        }

        row.LockSequence = sequence;
        row.IsLocked = action == LicenceAction.Lock;
        row.LockChangedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return (code, row);
    }

    /// <summary>
    /// The signing key. Refused if it is not the key whose public half this program carries: a licence signed by
    /// any other would not verify at a hospital, and finding that out after sending it is the worst time.
    /// </summary>
    private ECDsa LoadKey()
    {
        if (string.IsNullOrWhiteSpace(_options.SigningKeyPath) || !File.Exists(_options.SigningKeyPath))
        {
            throw new IssueException("There is no signing key on this machine, so nothing can be signed.");
        }

        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(File.ReadAllText(_options.SigningKeyPath));

            if (!string.IsNullOrWhiteSpace(_options.PublicKey)
                && Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != _options.PublicKey.Trim())
            {
                throw new IssueException(
                    "The signing key is not the one whose public half this program carries, so what it signs "
                    + "would not verify at a hospital. Check Licence:SigningKeyPath and Licence:PublicKey.");
            }

            return key;
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            key.Dispose();
            throw new IssueException($"The signing key could not be read: {e.Message}");
        }
        catch (IssueException)
        {
            key.Dispose();
            throw;
        }
    }
}
