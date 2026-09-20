namespace HospitalPm.Domain.Assets;

/// <summary>
/// One time a machine was moved from one place to another.
///
/// Machines are shifted between rooms and wards all the time, and a technician
/// sent to a room to service a machine needs to know it is not there any more,
/// and where it went. The register holds only where a machine is now; this holds
/// where it has been.
///
/// NO PATIENT DATA. A move names two places and the person who recorded it, never
/// the patient the machine was used for. See CLAUDE.md.
/// </summary>
public sealed class EquipmentMove
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int EquipmentId { get; set; }

    /// <summary>Where it was. Null for a machine's first placement, which is not a move.</summary>
    public int? FromLocationId { get; set; }

    public int ToLocationId { get; set; }

    /// <summary>Why: "Shifted to ICU for the night", "Back from repair". Optional.</summary>
    public string? Reason { get; set; }

    public int MovedByUserId { get; set; }

    public DateTime MovedAtUtc { get; set; }
}
