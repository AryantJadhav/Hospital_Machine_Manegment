namespace HospitalPm.Domain.Assets;

/// <summary>
/// The kind of paid maintenance contract a machine is under.
///
/// A machine with no contract has no value at all, not a "none" member, so the
/// column being empty already says it and nothing has to be kept in step.
/// Numbered with gaps, like <see cref="EquipmentStatus"/>.
/// </summary>
public enum MaintenanceContractType
{
    /// <summary>Annual Maintenance Contract: the vendor services the machine; parts are usually billed separately.</summary>
    Amc = 10,

    /// <summary>Comprehensive Maintenance Contract: servicing and the parts are both covered.</summary>
    Cmc = 20,
}
