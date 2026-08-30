namespace HospitalPm.Infrastructure.Labels;

/// <summary>
/// Everything printed on one asset tag.
///
/// Deliberately carries no patient or clinical field — an asset label is
/// stuck to a machine in a public corridor and photographed by anyone
/// walking past.
/// </summary>
public sealed record LabelData(
    string AssetTag,
    string EquipmentTypeName,
    string LocationName,
    string? SerialNumber);
