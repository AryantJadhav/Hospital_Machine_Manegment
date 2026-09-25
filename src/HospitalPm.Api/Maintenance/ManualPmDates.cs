using HospitalPm.Domain.Maintenance;
using EquipmentAsset = HospitalPm.Domain.Assets.Equipment;

namespace HospitalPm.Api.Maintenance;

/// <summary>
/// PM dates that are picked one by one instead of following a pattern.
///
/// A repeating schedule works its dates out from a start date and a frequency. This one has no
/// frequency: each date somebody chose is a PM of its own, and more can be added later. The
/// nightly job leaves these schedules alone, because there is nothing to work out.
/// </summary>
public static class ManualPmDates
{
    /// <summary>Five years of monthly PMs. More than that is a mistake, not a plan.</summary>
    public const int MaxDates = 60;

    /// <summary>
    /// The dates cleaned up: each once, earliest first. Or the reason they are not acceptable.
    /// A date already past is allowed, as a repeating schedule allows a start in the past: it is
    /// how a PM that was missed, or is being entered afterwards, gets on the record.
    /// </summary>
    public static (List<DateOnly>? Dates, string? Error) Clean(IReadOnlyList<DateOnly>? dates)
    {
        if (dates is null || dates.Count == 0)
        {
            return (null, "Choose at least one date for the PM.");
        }

        if (dates.Any(d => d.Year is < 2000 or > 2100))
        {
            return (null, "One of the PM dates is not a date.");
        }

        var clean = dates.Distinct().Order().ToList();
        return clean.Count > MaxDates
            ? (null, $"A machine can be given at most {MaxDates} PM dates at once.")
            : (clean, null);
    }

    /// <summary>The PMs for these dates, each in the state its date puts it in today.</summary>
    public static IEnumerable<PmTask> Tasks(PmSchedule schedule, EquipmentAsset? equipment, IEnumerable<DateOnly> dates, DateOnly today)
        => dates.Select(due => new PmTask
        {
            TenantId = schedule.TenantId,
            Schedule = schedule,
            EquipmentId = schedule.EquipmentId,
            Equipment = equipment,
            DueDate = due,
            Status = PmTask.StatusOn(today, due, schedule.GraceDays),
        });
}
