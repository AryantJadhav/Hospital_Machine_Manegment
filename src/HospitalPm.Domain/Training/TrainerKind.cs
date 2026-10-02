namespace HospitalPm.Domain.Training;

/// <summary>Where a session's trainer came from. Stored as the text below, so the column reads plainly in a backup.</summary>
public static class TrainerKind
{
    /// <summary>The vendor or the machine's manufacturer sent someone.</summary>
    public const string Vendor = "Vendor";

    /// <summary>One of the hospital's own team ran it.</summary>
    public const string InHouse = "InHouse";

    public static bool IsKnown(string? value) => value is Vendor or InHouse;

    /// <summary>What a person reads on screen and on the printed report.</summary>
    public static string? Label(string? value) => value switch
    {
        Vendor => "Vendor / Manufacturer",
        InHouse => "In-house team",
        _ => null,
    };
}
