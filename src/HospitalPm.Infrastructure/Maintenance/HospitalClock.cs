using Microsoft.Extensions.Options;

namespace HospitalPm.Infrastructure.Maintenance;

public sealed class ScheduleOptions
{
    public const string SectionName = "Schedule";

    /// <summary>
    /// The hospital's offset from UTC, in minutes. Default +05:30 for India.
    ///
    /// A fixed offset rather than an IANA time zone id on purpose. Resolving
    /// "Asia/Kolkata" needs the OS time zone database, which a minimal Linux
    /// container often does not ship, and this app is built with invariant
    /// globalization precisely to avoid depending on host data files. India
    /// has never observed daylight saving, so a fixed offset is not an
    /// approximation here — it is exact.
    ///
    /// A hospital in a DST region would need real zone data. That is a
    /// deliberate limitation, recorded here rather than discovered later.
    /// </summary>
    public int UtcOffsetMinutes { get; set; } = 330;

    /// <summary>
    /// How far ahead occurrences are generated. Sixty days lets a biomedical
    /// head plan next month's workload without filling the table with years
    /// of rows nobody will look at.
    /// </summary>
    public int HorizonDays { get; set; } = 60;
}

/// <summary>
/// What "today" means to this hospital.
///
/// PM due dates are dates, not instants. Computing them from UTC would put
/// an Indian hospital on yesterday's date between midnight and 05:30 local,
/// so a PM due today would show as due tomorrow for the first five and a
/// half hours of every day — and overdue counts would flip around dawn.
/// </summary>
public sealed class HospitalClock(TimeProvider clock, IOptions<ScheduleOptions> options)
{
    private readonly ScheduleOptions _options = options.Value;

    public TimeSpan Offset => TimeSpan.FromMinutes(_options.UtcOffsetMinutes);

    public DateOnly Today() => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime.Add(Offset));

    public DateTime UtcNow() => clock.GetUtcNow().UtcDateTime;
}
