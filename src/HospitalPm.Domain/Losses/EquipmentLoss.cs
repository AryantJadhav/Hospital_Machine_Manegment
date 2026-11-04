using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Locations;
using EquipmentAsset = HospitalPm.Domain.Assets.Equipment;

namespace HospitalPm.Domain.Losses;

/// <summary>How the machine was lost.</summary>
public enum LossKind
{
    /// <summary>Misplaced or gone missing, and not found.</summary>
    Lost = 10,

    /// <summary>Taken without permission.</summary>
    Stolen = 20,

    /// <summary>Lost while it was away: with a vendor, a courier, or on its way back from one.</summary>
    LostAway = 30,
}

/// <summary>
/// Where a loss stands on its way up: the department writes it up, a biomedical engineer looks into it and approves it,
/// and the head of Biomedical makes the final decision. A loss written up by an engineer or the head starts at the
/// head's desk, because the engineer's step is theirs already.
/// </summary>
public enum LossStatus
{
    /// <summary>Written up by a department, and waiting for a biomedical engineer.</summary>
    Reported = 10,

    /// <summary>A biomedical engineer is looking for it, or into how it went. Not yet approved.</summary>
    Investigating = 20,

    /// <summary>An engineer has approved it, and it waits for the head of Biomedical's final decision.</summary>
    AwaitingDecision = 25,

    /// <summary>It was found and is back. Final.</summary>
    Recovered = 30,

    /// <summary>It is not coming back, and the hospital has accepted the loss. Final.</summary>
    WrittenOff = 40,

    /// <summary>Written up by mistake: the machine was not lost. Kept, because its number was issued. Final.</summary>
    Cancelled = 50,
}

/// <summary>What each value is called to a person, in one place, so the screen, the API and the printed report say the same thing.</summary>
public static class LossWords
{
    public static string Kind(LossKind kind) => kind switch
    {
        LossKind.Lost => "Lost",
        LossKind.Stolen => "Stolen",
        _ => "Lost while away",
    };

    public static string Status(LossStatus status) => status switch
    {
        LossStatus.Reported => "Waiting for a biomedical engineer",
        LossStatus.Investigating => "Being looked into by an engineer",
        LossStatus.AwaitingDecision => "Waiting for the head's decision",
        LossStatus.Recovered => "Found and recovered",
        LossStatus.WrittenOff => "Written off",
        _ => "Cancelled",
    };

    /// <summary>Recovered, written off and cancelled are the end of it.</summary>
    public static bool IsFinal(LossStatus status) => status is LossStatus.Recovered or LossStatus.WrittenOff or LossStatus.Cancelled;

    /// <summary>Still being dealt with: the machine is missing and a step is waiting.</summary>
    public static bool IsOpen(LossStatus status) => !IsFinal(status);
}

/// <summary>
/// The loss of a machine: it was misplaced, stolen, or lost while it was away.
///
/// A department or an engineer writes it up, the biomedical team looks for it and into how it went, and it ends
/// one of two ways: the machine is found, or the hospital accepts the loss and writes it off. While it is open the
/// register shows the machine as <see cref="EquipmentStatus.Lost"/>, so it drops out of maintenance and cannot be
/// sent out, and a found machine goes back to the status it had. The record is kept for good: it answers
/// "what have we lost, what was it worth, and where does it keep happening?", which a loose file of police
/// complaints cannot.
///
/// NO PATIENT DATA. A loss is about a machine. It never names or describes a patient, and the staff member who last
/// had it, if one is named at all, is staff. See CLAUDE.md.
/// </summary>
public sealed class EquipmentLoss
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int EquipmentId { get; set; }

    /// <summary>
    /// Where the machine was meant to be when this was written up, kept as written: the machine is not in that place any
    /// more, and the report must still say where it went from.
    /// </summary>
    public int LocationId { get; set; }

    public LossKind Kind { get; set; }

    /// <summary>The day it was found to be missing.</summary>
    public DateOnly NoticedOn { get; set; }

    /// <summary>The last day anyone is sure it was there, when that is known. Never after <see cref="NoticedOn"/>.</summary>
    public DateOnly? LastSeenOn { get; set; }

    /// <summary>Where it was last seen: "Recovery bay, on the trolley by bed 4". Free text, and never about a patient.</summary>
    public string? LastSeenPlace { get; set; }

    /// <summary>What happened, in the reporter's own words.</summary>
    public required string Description { get; set; }

    /// <summary>The staff member who last had it, when that is worth recording. Staff only.</summary>
    public string? LastHandledBy { get; set; }

    /// <summary>The number of the police complaint (FIR), when one was made.</summary>
    public string? PoliceReportNumber { get; set; }

    /// <summary>The day the police were told.</summary>
    public DateOnly? PoliceReportedOn { get; set; }

    /// <summary>What the machine was worth, in rupees: the register's cost when it was written up, or what the reporter said. Null when nobody knows.</summary>
    public decimal? EstimatedValue { get; set; }

    public LossStatus Status { get; set; } = LossStatus.Reported;

    /// <summary>What the biomedical engineer found out about how it went. Required to approve the loss, and so to write it off.</summary>
    public string? Findings { get; set; }

    /// <summary>What was done, or will be, so it does not happen again: tagging, a locked store, a sign-out book.</summary>
    public string? CorrectiveAction { get; set; }

    /// <summary>The day it was found. Set when, and only when, the loss is recovered.</summary>
    public DateOnly? RecoveredOn { get; set; }

    /// <summary>
    /// What the machine's status on the register was at the moment it was marked lost. A found machine goes back to it, so
    /// one that was in store comes back to store and not to a ward.
    /// </summary>
    public EquipmentStatus? EquipmentStatusBefore { get; set; }

    /// <summary>
    /// The biomedical engineer who approved it for the head's decision. A loss an engineer or the head wrote up is approved by
    /// them at the moment they write it. Cleared if the head sends it back. Null while it waits for an engineer.
    /// </summary>
    public int? ApprovedByUserId { get; set; }

    public DateTime? ApprovedAtUtc { get; set; }

    /// <summary>The head's own words with the final decision, or why a loss was sent back to the engineer.</summary>
    public string? DecisionNotes { get; set; }

    public int ReportedByUserId { get; set; }

    public DateTime ReportedAtUtc { get; set; }

    /// <summary>When it was recovered, written off or cancelled.</summary>
    public DateTime? ClosedAtUtc { get; set; }

    /// <summary>Who ended it: the head for a write-off, and an engineer or the head when it was found or rejected.</summary>
    public int? ClosedByUserId { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public EquipmentAsset? Equipment { get; set; }

    public Location? Location { get; set; }

    /// <summary>What to quote and what is printed: LOSS-2026-00012.</summary>
    public string Reference => ReferenceFor(ReportedAtUtc, Id);

    public static string ReferenceFor(DateTime reportedAtUtc, int id) => $"LOSS-{reportedAtUtc.Year}-{id:D5}";
}
