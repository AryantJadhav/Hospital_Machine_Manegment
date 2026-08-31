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
public sealed record Licence(
    Guid LicenceId,
    string HospitalName,
    DateOnly IssuedOn,
    DateOnly? ExpiresOn,
    IReadOnlyList<string> Modules,
    int? MaxEquipment,
    string? Notes);

public enum LicenceState
{
    /// <summary>Signed, current, and within any limits it sets.</summary>
    Valid = 10,

    /// <summary>
    /// No licence file present. The app runs — a pilot install has to work
    /// before anyone has issued anything — and says so.
    /// </summary>
    Missing = 20,

    /// <summary>Past its expiry date. Still runs, loudly.</summary>
    Expired = 30,

    /// <summary>
    /// Present but not trustworthy: wrong signature, edited payload, or
    /// unreadable file. Treated as no licence at all, never as valid.
    /// </summary>
    Invalid = 40,
}

/// <summary>
/// The verdict on this installation's licence.
///
/// Nothing here blocks the application. A hospital whose licence lapsed still
/// needs to open the ventilator's service history at two in the morning, and
/// a maintenance system that locks its own users out over billing is a
/// system that gets ripped out. Expiry is a conversation, not a kill switch.
/// </summary>
/// <param name="State">Valid, Missing, Expired or Invalid.</param>
/// <param name="Licence">The payload, when it could be read and trusted.</param>
/// <param name="Message">One line, written for a hospital administrator.</param>
public sealed record LicenceStatus(LicenceState State, Licence? Licence, string Message)
{
    public bool IsLicensed => State == LicenceState.Valid;

    /// <summary>
    /// Whether a paid module may be used. Core features never consult this.
    /// </summary>
    public bool HasModule(string module) =>
        State == LicenceState.Valid &&
        Licence is not null &&
        Licence.Modules.Contains(module, StringComparer.OrdinalIgnoreCase);
}
