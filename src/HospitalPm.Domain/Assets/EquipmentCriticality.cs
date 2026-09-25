namespace HospitalPm.Domain.Assets;

/// <summary>
/// How much a patient's care depends on this machine working.
///
/// This is a property of the machine's use, not a clinical record: it says how
/// bad it is when the machine is down, and so how closely to maintain it. It
/// carries no patient information.
///
/// The numbers leave gaps, like <see cref="EquipmentStatus"/>, so a level can be
/// added between two later without renumbering rows already stored.
/// </summary>
public enum EquipmentCriticality
{
    /// <summary>Its failure does not affect care: a weighing scale, a hospital bed light.</summary>
    NonCritical = 10,

    /// <summary>Its failure delays or degrades care but there is a workaround.</summary>
    SemiCritical = 20,

    /// <summary>Its failure puts a patient at risk: a ventilator, a defibrillator.</summary>
    Critical = 30,
}
