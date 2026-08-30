namespace HospitalPm.Domain.WorkOrders;

/// <summary>
/// The work order state machine.
///
/// Kept as data rather than scattered through endpoint code, so the rules can
/// be read in one place and asserted directly. The same table is mirrored by
/// a database trigger — the application decides what to offer, the database
/// decides what is possible.
/// </summary>
public static class WorkOrderTransitions
{
    private static readonly Dictionary<WorkOrderStatus, WorkOrderStatus[]> Allowed = new()
    {
        [WorkOrderStatus.Reported] =
        [
            WorkOrderStatus.Assigned,
            WorkOrderStatus.InProgress, // an engineer who picks up their own ticket
            WorkOrderStatus.Cancelled,
        ],

        [WorkOrderStatus.Assigned] =
        [
            WorkOrderStatus.InProgress,
            // Back to the pool. Reassignment is routine: the named engineer
            // is on leave, or the fault turns out to belong to someone else.
            WorkOrderStatus.Reported,
            WorkOrderStatus.OnHold,
            WorkOrderStatus.Cancelled,
        ],

        [WorkOrderStatus.InProgress] =
        [
            WorkOrderStatus.Resolved,
            WorkOrderStatus.OnHold,
            WorkOrderStatus.Cancelled,
        ],

        [WorkOrderStatus.OnHold] =
        [
            WorkOrderStatus.InProgress,
            WorkOrderStatus.Assigned,
            WorkOrderStatus.Cancelled,
        ],

        [WorkOrderStatus.Resolved] =
        [
            WorkOrderStatus.Closed,
            // Reopening matters. "We thought it was fixed" is the single most
            // common thing that happens to a resolved ticket, and forcing a
            // new ticket would break the link between a fault and its real
            // repair history.
            WorkOrderStatus.InProgress,
        ],

        // Terminal.
        [WorkOrderStatus.Closed] = [],
        [WorkOrderStatus.Cancelled] = [],
    };

    public static IReadOnlyList<WorkOrderStatus> From(WorkOrderStatus status)
        => Allowed.TryGetValue(status, out var next) ? next : [];

    public static bool CanMove(WorkOrderStatus from, WorkOrderStatus to)
        => from != to && From(from).Contains(to);

    public static bool IsTerminal(WorkOrderStatus status) => From(status).Count == 0;

    /// <summary>
    /// Explains a refusal in terms an operator can act on, rather than
    /// echoing two enum names back at them.
    /// </summary>
    public static string Explain(WorkOrderStatus from, WorkOrderStatus to)
    {
        if (from == to)
        {
            return $"This work order is already {Describe(from)}.";
        }

        if (IsTerminal(from))
        {
            return from == WorkOrderStatus.Closed
                ? "This work order is closed. Raise a new one if the fault has returned."
                : "This work order was cancelled and cannot be reopened.";
        }

        var options = From(from).Select(Describe);
        return $"A work order that is {Describe(from)} can only move to: {string.Join(", ", options)}.";
    }

    private static string Describe(WorkOrderStatus status) => status switch
    {
        WorkOrderStatus.Reported => "reported",
        WorkOrderStatus.Assigned => "assigned",
        WorkOrderStatus.InProgress => "in progress",
        WorkOrderStatus.OnHold => "on hold",
        WorkOrderStatus.Resolved => "resolved",
        WorkOrderStatus.Closed => "closed",
        WorkOrderStatus.Cancelled => "cancelled",
        _ => status.ToString().ToLowerInvariant(),
    };
}
