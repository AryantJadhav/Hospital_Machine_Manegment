namespace HospitalPm.Domain.Maintenance;

/// <summary>
/// How often a PM falls due.
///
/// The named intervals are the ones NABH evidence is usually organised
/// around; Custom exists because manufacturers specify their own and a
/// hospital should not have to lie about a 45-day service interval.
/// </summary>
public enum PmFrequency
{
    Monthly = 10,
    Quarterly = 20,
    HalfYearly = 30,
    Yearly = 40,

    /// <summary>Interval given in days on the schedule.</summary>
    Custom = 90,
}

public static class PmFrequencyExtensions
{
    /// <summary>
    /// Months per occurrence, or null for Custom.
    ///
    /// Calendar months rather than a fixed day count: a quarterly PM anchored
    /// to 31 January should fall on 30 April, not on some drifting date that
    /// depends on how many 31-day months happened to pass.
    /// </summary>
    public static int? Months(this PmFrequency frequency) => frequency switch
    {
        PmFrequency.Monthly => 1,
        PmFrequency.Quarterly => 3,
        PmFrequency.HalfYearly => 6,
        PmFrequency.Yearly => 12,
        _ => null,
    };
}
