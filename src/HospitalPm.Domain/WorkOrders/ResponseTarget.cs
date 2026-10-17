namespace HospitalPm.Domain.WorkOrders;

/// <summary>
/// How quickly an engineer is expected to respond to a fault of one priority: the longest it should be from the moment
/// the fault is raised until an engineer has reached the machine and pressed In progress.
///
/// One row per priority, set by the biomedical head. A response time is NOT the time to fix the fault; it ends when the
/// work starts.
/// </summary>
public sealed class ResponseTarget
{
    public int TenantId { get; set; } = 1;

    public WorkOrderPriority Priority { get; set; }

    /// <summary>The longest wanted from raised to In progress, in minutes. A clock that does not stop at night or on a holiday.</summary>
    public int Minutes { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Where a fault stands against the response time its priority asks for.</summary>
public enum ResponseState
{
    /// <summary>No target to judge against, or the fault never reached In progress (cancelled, or resolved without being started).</summary>
    NotApplicable = 0,

    /// <summary>Nobody has started it yet and the time allowed has not run out.</summary>
    Waiting = 1,

    /// <summary>Nobody has started it and the time allowed has run out.</summary>
    Overdue = 2,

    /// <summary>An engineer started it within the time allowed.</summary>
    Met = 3,

    /// <summary>An engineer started it, but later than the time allowed.</summary>
    Missed = 4,
}

public sealed record ResponseAssessment(
    ResponseState State,
    int? TargetMinutes,
    DateTime? DueAtUtc,
    int? ResponseMinutes,
    int? OverdueMinutes);

public static class ResponseRules
{
    /// <summary>Shorter than a minute is not a target a person can meet; longer than 30 days is not a response.</summary>
    public const int MinMinutes = 1;

    public const int MaxMinutes = 43_200;

    /// <summary>
    /// What a hospital starts with, before the head changes it: a critical fault answered within 15 minutes, a high one
    /// within an hour, a medium one within 4 hours, a low one within a day. Starting points, not standards.
    /// </summary>
    public static readonly IReadOnlyDictionary<WorkOrderPriority, int> Defaults = new Dictionary<WorkOrderPriority, int>
    {
        [WorkOrderPriority.Critical] = 15,
        [WorkOrderPriority.High] = 60,
        [WorkOrderPriority.Medium] = 240,
        [WorkOrderPriority.Low] = 1440,
    };

    public const int MinReasonLength = 3;

    public const int MaxReasonLength = 500;

    /// <summary>
    /// Why a solved fault's started time cannot be changed to this, or null when it can. Only the head corrects it, and only
    /// once the fault is solved: while it is open, the engineer's In progress click is what sets it. The time has to fit
    /// the fault's own story (after it was raised, not after it was resolved, not in the future) and the head has to say why.
    /// </summary>
    public static string? StartedTimeProblem(
        WorkOrderStatus status,
        DateTime reportedAtUtc,
        DateTime? resolvedAtUtc,
        DateTime newStartedAtUtc,
        DateTime nowUtc,
        string? reason)
    {
        if (status is not (WorkOrderStatus.Resolved or WorkOrderStatus.Closed))
        {
            return "Only a solved fault can have its started time corrected. While it is open, pressing In progress sets it.";
        }

        var why = reason?.Trim() ?? string.Empty;
        if (why.Length < MinReasonLength)
        {
            return "Say why the time is being corrected, in a few words.";
        }

        if (why.Length > MaxReasonLength)
        {
            return $"Keep the reason to {MaxReasonLength} characters or fewer.";
        }

        if (newStartedAtUtc < reportedAtUtc)
        {
            return "The work cannot have started before the fault was raised.";
        }

        if (resolvedAtUtc is { } resolved && newStartedAtUtc > resolved)
        {
            return "The work cannot have started after the fault was resolved.";
        }

        if (newStartedAtUtc > nowUtc)
        {
            return "The started time cannot be in the future.";
        }

        return null;
    }

    public static string? Problem(int minutes) =>
        minutes is < MinMinutes or > MaxMinutes
            ? $"A response time must be between {MinMinutes} minute and {MaxMinutes / 1440} days."
            : null;

    /// <summary>
    /// Judges one fault. The response is the time from the fault being raised to the engineer pressing In progress for the
    /// first time; a fault put back In progress later does not change it. The clock runs all day and night.
    /// </summary>
    /// <param name="status">Where the fault is now. A cancelled fault, or one closed without ever being started, is not judged.</param>
    public static ResponseAssessment Assess(
        DateTime reportedAtUtc,
        DateTime? startedAtUtc,
        int? targetMinutes,
        WorkOrderStatus status,
        DateTime nowUtc)
    {
        if (targetMinutes is not { } target)
        {
            return new ResponseAssessment(ResponseState.NotApplicable, null, null, Minutes(reportedAtUtc, startedAtUtc), null);
        }

        var due = reportedAtUtc.AddMinutes(target);

        if (startedAtUtc is { } started)
        {
            var took = Minutes(reportedAtUtc, started);
            return new ResponseAssessment(started <= due ? ResponseState.Met : ResponseState.Missed, target, due, took, null);
        }

        // Never started and not going to be: cancelled, or resolved / closed by someone who never pressed In progress.
        if (status is WorkOrderStatus.Cancelled or WorkOrderStatus.Resolved or WorkOrderStatus.Closed)
        {
            return new ResponseAssessment(ResponseState.NotApplicable, target, due, null, null);
        }

        return nowUtc > due
            ? new ResponseAssessment(ResponseState.Overdue, target, due, null, (int)Math.Floor((nowUtc - due).TotalMinutes))
            : new ResponseAssessment(ResponseState.Waiting, target, due, null, null);
    }

    // Whole minutes, never negative: a clock set wrong must not turn into a response before the fault was raised.
    private static int? Minutes(DateTime reportedAtUtc, DateTime? startedAtUtc) =>
        startedAtUtc is { } s ? (int)Math.Max(0, Math.Round((s - reportedAtUtc).TotalMinutes)) : null;
}
