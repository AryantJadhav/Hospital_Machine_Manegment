namespace HospitalPm.Domain.Licensing;

/// <summary>
/// What a licence says. Signed by us, verified offline on the hospital's own
/// machine with a public key compiled into the binary.
/// </summary>
/// <param name="LicenceId">Identifies this licence when a hospital rings up about it.</param>
/// <param name="HospitalName">Shown in the app and on printed reports.</param>
/// <param name="IssuedOn">Date of issue, for support.</param>
/// <param name="ExpiresOn">Last day the licence is current. Null means perpetual.</param>
/// <param name="Modules">Enabled module keys. Core is always implied.</param>
/// <param name="MaxEquipment">Cap on equipment records, or null for no cap.</param>
/// <param name="Notes">Free text for support — the reseller, the PO number.</param>
/// <param name="DurationDays">
/// A licence that runs for a length of time rather than to a calendar date: it
/// starts counting on the day it is first installed. Lets a pilot key be issued
/// before the install date is known. <see cref="ExpiresOn"/> and this are
/// alternatives; if both are present, the earlier end wins.
/// </param>
public sealed record Licence(
    Guid LicenceId,
    string HospitalName,
    DateOnly IssuedOn,
    DateOnly? ExpiresOn,
    IReadOnlyList<string> Modules,
    int? MaxEquipment,
    string? Notes,
    int? DurationDays = null);

public enum LicenceState
{
    /// <summary>Signed, current, and within any limits it sets.</summary>
    Valid = 10,

    /// <summary>
    /// No licence file present. The app runs — a pilot install has to work
    /// before anyone has issued anything — and says so.
    /// </summary>
    Missing = 20,

    /// <summary>
    /// Past its expiry date but inside the grace period. Everything still works,
    /// loudly, so a renewal that is a few days late costs a ward nothing.
    /// </summary>
    Expired = 30,

    /// <summary>
    /// Past expiry and past the grace period. Records can be opened and printed
    /// but nothing new can be recorded until a renewal is installed.
    /// </summary>
    ReadOnly = 35,

    /// <summary>
    /// Present but not trustworthy: wrong signature, edited payload, or
    /// unreadable file. Treated as no licence at all, never as valid.
    /// </summary>
    Invalid = 40,
}

/// <summary>
/// The verdict on this installation's licence.
///
/// An expired licence never locks anyone out. A hospital whose licence lapsed
/// still needs to open the ventilator's service history at two in the morning,
/// and a maintenance system that locks its own users out over billing is a
/// system that gets ripped out. After the grace period the software becomes
/// read-only: everything can be viewed and printed, nothing new is recorded.
/// </summary>
/// <param name="State">Valid, Missing, Expired or Invalid.</param>
/// <param name="Licence">The payload, when it could be read and trusted.</param>
/// <param name="Message">One line, written for a hospital administrator.</param>
/// <param name="EffectiveExpiry">
/// The last day the licence is current: its fixed date, or its start plus its
/// duration. Null for a perpetual licence.
/// </param>
/// <param name="ReadOnlyFrom">The first day the software is read-only. Null for a perpetual licence.</param>
public sealed record LicenceStatus(
    LicenceState State,
    Licence? Licence,
    string Message,
    DateOnly? EffectiveExpiry = null,
    DateOnly? ReadOnlyFrom = null)
{
    public bool IsLicensed => State == LicenceState.Valid;

    /// <summary>Whether recording anything new is refused.</summary>
    public bool IsReadOnly => State == LicenceState.ReadOnly;

    /// <summary>
    /// Whether a paid module may be used. Core features never consult this.
    /// </summary>
    public bool HasModule(string module) =>
        State == LicenceState.Valid &&
        Licence is not null &&
        Licence.Modules.Contains(module, StringComparer.OrdinalIgnoreCase);
}
