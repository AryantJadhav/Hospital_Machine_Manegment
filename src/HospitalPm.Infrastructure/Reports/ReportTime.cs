using System.Globalization;

namespace HospitalPm.Infrastructure.Reports;

/// <summary>
/// Turns a stored UTC timestamp into the hospital's own clock for printing.
///
/// Everything is stored in UTC, but a printed certificate is read by someone
/// at the hospital. A PM signed at 00:27 on the 20th in India is 18:57 on the
/// 19th in UTC, and printing that beside the word "local" put both the hour
/// and the date wrong on the one document an auditor is handed.
/// </summary>
public static class ReportTime
{
    /// <summary>"20/09/2026 00:27", on the hospital's clock.</summary>
    public static string DateTime(DateTime utc, TimeSpan offset)
        => (utc + offset).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"20/09 00:27", for timeline rows that sit under a dated heading.</summary>
    public static string DayAndTime(DateTime utc, TimeSpan offset)
        => (utc + offset).ToString("dd/MM HH:mm", CultureInfo.InvariantCulture);
}
