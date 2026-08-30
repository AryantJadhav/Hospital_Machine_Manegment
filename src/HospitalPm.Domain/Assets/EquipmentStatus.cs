namespace HospitalPm.Domain.Assets;

/// <summary>
/// Lifecycle state of a physical asset.
///
/// Deliberately small. This is the asset's own state, not the state of any
/// work order against it — a machine can be InService while an open
/// breakdown ticket exists, and conflating the two makes both unreliable.
/// </summary>
public enum EquipmentStatus
{
    /// <summary>Received but not yet commissioned.</summary>
    InStore = 10,

    InService = 20,

    /// <summary>Out of service pending repair.</summary>
    UnderRepair = 30,

    /// <summary>Withdrawn from service, retained on the register for audit history.</summary>
    Condemned = 40,

    /// <summary>Disposed of. Kept because PM certificates referencing it must stay readable.</summary>
    Disposed = 50,
}
