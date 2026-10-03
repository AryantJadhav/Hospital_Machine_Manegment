namespace HospitalPm.Domain.Identity;

/// <summary>
/// A department (or any place in the location tree) that one person belongs to.
///
/// What a Department user may see is limited to the equipment in the places listed here, and in
/// everything under them: giving someone a floor gives them every ward on it. A person may have
/// several. At most one row for each person and place.
/// </summary>
public sealed class UserLocation
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int UserId { get; set; }

    public int LocationId { get; set; }

    public int AssignedByUserId { get; set; }

    public DateTime AssignedAtUtc { get; set; }
}
