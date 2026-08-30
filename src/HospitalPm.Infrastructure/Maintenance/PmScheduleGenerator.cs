using HospitalPm.Domain.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HospitalPm.Infrastructure.Maintenance;

public sealed record GenerationResult(int Created, int Transitioned, int SchedulesConsidered);

/// <summary>
/// Creates upcoming PM occurrences and moves existing ones through
/// Scheduled → Due → Overdue.
///
/// Runs nightly on Hangfire, and is written to be safe to run at any time,
/// any number of times. A hospital server gets restarted, the job gets
/// triggered by hand from the dashboard, and a clock change can fire it
/// twice — none of which may produce duplicate work for a technician.
/// </summary>
public sealed class PmScheduleGenerator(
    HospitalPmDbContext db,
    HospitalClock clock,
    IOptions<ScheduleOptions> options)
{
    private readonly ScheduleOptions _options = options.Value;

    public async Task<GenerationResult> RunAsync(CancellationToken ct = default)
    {
        var today = clock.Today();
        var horizon = today.AddDays(_options.HorizonDays);

        var schedules = await db.PmSchedules
            .Where(s => s.IsActive)
            .Select(s => new
            {
                s.Id,
                s.EquipmentId,
                s.TenantId,
                s.Frequency,
                s.IntervalDays,
                s.AnchorDate,
                s.GraceDays,
            })
            .ToListAsync(ct);

        var created = 0;

        foreach (var schedule in schedules)
        {
            // Existing due dates are loaded rather than relying on the unique
            // index to reject duplicates: letting a constraint violation abort
            // the batch would mean one bad schedule stops every other one.
            var existing = await db.PmTasks
                .Where(t => t.PmScheduleId == schedule.Id)
                .Select(t => t.DueDate)
                .ToListAsync(ct);

            var known = existing.ToHashSet();

            // Generation starts from the anchor, not from today, so a schedule
            // added for a machine that has been in service for two years still
            // produces the overdue history an auditor expects to see.
            var occurrences = PmDueDates.Between(
                schedule.AnchorDate,
                schedule.Frequency,
                schedule.IntervalDays,
                from: schedule.AnchorDate,
                horizon: horizon);

            foreach (var due in occurrences)
            {
                if (!known.Add(due))
                {
                    continue;
                }

                db.PmTasks.Add(new PmTask
                {
                    TenantId = schedule.TenantId,
                    PmScheduleId = schedule.Id,
                    EquipmentId = schedule.EquipmentId,
                    DueDate = due,
                    Status = PmTask.StatusOn(today, due, schedule.GraceDays),
                });

                created++;
            }
        }

        if (created > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        var transitioned = await AdvanceStatusesAsync(today, ct);

        return new GenerationResult(created, transitioned, schedules.Count);
    }

    /// <summary>
    /// Moves open tasks into Due and Overdue as dates pass.
    ///
    /// Completed and Skipped are never touched: a PM done on time does not
    /// become overdue because a later job run noticed the date has passed.
    /// </summary>
    private async Task<int> AdvanceStatusesAsync(DateOnly today, CancellationToken ct)
    {
        // Set-based rather than row-by-row. At 15,000 assets on quarterly
        // schedules this is tens of thousands of rows, and loading them into
        // the change tracker nightly would be slow and pointless.
        var toDue = await db.PmTasks
            .Where(t => t.Status == PmTaskStatus.Scheduled
                        && t.DueDate <= today
                        && today <= t.DueDate.AddDays(t.Schedule!.GraceDays))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, PmTaskStatus.Due), ct);

        var toOverdue = await db.PmTasks
            .Where(t => (t.Status == PmTaskStatus.Scheduled || t.Status == PmTaskStatus.Due)
                        && today > t.DueDate.AddDays(t.Schedule!.GraceDays))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, PmTaskStatus.Overdue), ct);

        return toDue + toOverdue;
    }
}
