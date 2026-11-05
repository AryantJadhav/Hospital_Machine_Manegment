namespace HospitalPm.Domain.Locations;

/// <summary>
/// Rungs of the location hierarchy, ordered outermost to innermost.
///
/// Stored as the numeric value so ordering comparisons work in SQL, and so
/// inserting a level later does not renumber existing rows — hence the gaps.
/// </summary>
public enum LocationLevel
{
    Organisation = 10,
    Site = 20,
    Building = 30,
    Floor = 40,
    Department = 50,
    Room = 60,

    /// <summary>
    /// One bed, in a room or straight in a ward. The most exact place a machine can be filed against: "ICU Room 2 · Bed 3".
    /// A bed is deeper than a room, so levels above it are unchanged.
    /// </summary>
    Bed = 70,
}
