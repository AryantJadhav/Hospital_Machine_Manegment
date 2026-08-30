namespace HospitalPm.Domain.Locations;

/// <summary>
/// A place equipment can live: organisation, site, building, floor,
/// department or room.
///
/// One self-referencing table rather than six tables, because real hospitals
/// skip rungs. A single-building nursing home has no Site or Floor worth
/// modelling, and a six-table hierarchy would force empty placeholder rows
/// through every join and every screen. The level column records which rung
/// a row actually occupies; the parent chain records where it sits.
///
/// This is also the only thing equipment is ever linked to. Equipment is
/// never linked to a patient — see CLAUDE.md.
/// </summary>
public sealed class Location
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int? ParentId { get; set; }

    public LocationLevel Level { get; set; }

    /// <summary>Stable key used by Excel import to match an existing location.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// Materialised ancestor path, "/1/4/17/", including this row's own id.
    ///
    /// Kept so "everything under this site" is an indexed prefix scan rather
    /// than a recursive CTE per query. A hospital asking for PM compliance
    /// across a site runs that query constantly, and recursion over 15,000
    /// assets is the version that gets slow in year two.
    ///
    /// Maintained by a database trigger, not application code, so a direct
    /// SQL insert during an import cannot leave it wrong.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Depth in the tree, 0 for a root. Derived alongside Path.</summary>
    public int Depth { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public Location? Parent { get; set; }

    public ICollection<Location> Children { get; set; } = [];
}
