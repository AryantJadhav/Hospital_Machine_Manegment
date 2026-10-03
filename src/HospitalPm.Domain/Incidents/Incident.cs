using HospitalPm.Domain.Locations;
using EquipmentAsset = HospitalPm.Domain.Assets.Equipment;

namespace HospitalPm.Domain.Incidents;

/// <summary>What happened to the machine.</summary>
public enum IncidentType
{
    /// <summary>Dropped, knocked off a stand or table, or fell from a trolley or bed rail.</summary>
    Fall = 10,

    /// <summary>Rough, wrong or unauthorised handling: the wrong cable, forced on a stand, used outside what it is for.</summary>
    Mishandling = 20,

    /// <summary>Liquid got into it: a spill, a drip from above, a cleaning fluid.</summary>
    LiquidDamage = 30,

    /// <summary>Hit by a trolley, a bed, a door or another machine.</summary>
    Collision = 40,

    /// <summary>Not where it should be and not found.</summary>
    Missing = 50,

    Other = 90,
}

/// <summary>What state the machine was left in. Assessed by the department that reports it and corrected by the biomedical team after they have looked.</summary>
public enum DamageLevel
{
    None = 10,

    /// <summary>Marked or scratched, still works.</summary>
    Minor = 20,

    /// <summary>Stopped working, or cannot be trusted, and needs repair.</summary>
    Major = 30,

    /// <summary>Not worth repairing.</summary>
    BeyondRepair = 40,
}

public enum IncidentStatus
{
    /// <summary>Written up by the department, not yet looked at.</summary>
    Reported = 10,

    /// <summary>The biomedical team is looking into it.</summary>
    InReview = 20,

    /// <summary>Looked into, with what caused it written down. Final.</summary>
    Closed = 30,
}

/// <summary>
/// What each value is called to a person, in one place, so the screen, the API and the printed report say the same thing.
/// </summary>
public static class IncidentWords
{
    public static string Type(IncidentType type) => type switch
    {
        IncidentType.Fall => "Fall or drop",
        IncidentType.Mishandling => "Mishandling",
        IncidentType.LiquidDamage => "Liquid damage",
        IncidentType.Collision => "Collision",
        IncidentType.Missing => "Missing",
        _ => "Other",
    };

    public static string Damage(DamageLevel level) => level switch
    {
        DamageLevel.None => "No visible damage",
        DamageLevel.Minor => "Minor damage, still works",
        DamageLevel.Major => "Major damage, needs repair",
        _ => "Beyond repair",
    };

    public static string Status(IncidentStatus status) => status switch
    {
        IncidentStatus.Reported => "Reported",
        IncidentStatus.InReview => "Under review",
        _ => "Closed",
    };
}

/// <summary>
/// An incident with a machine: it was dropped, mishandled, knocked, soaked or lost.
///
/// A department writes it up, the biomedical team looks into it and says what caused it, and the hospital
/// keeps the record. It answers "how many machines have we lost to being dropped, and where?" and
/// "has this machine been mishandled before?", which a printed incident book cannot.
///
/// NO PATIENT DATA. An incident is about a machine. It never names or describes a patient, and the staff
/// member involved, if one is named at all, is staff. If something that happened to a patient is the real
/// subject, it belongs in the hospital's own clinical incident system, not here. See CLAUDE.md.
/// </summary>
public sealed class Incident
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int EquipmentId { get; set; }

    /// <summary>
    /// Where the machine was when this was written up, kept as written. The machine may be moved, or sent for repair,
    /// afterwards, and the report must still say where the incident was dealt with.
    /// </summary>
    public int LocationId { get; set; }

    public IncidentType Type { get; set; }

    /// <summary>The day it happened, which may be before the day it was reported.</summary>
    public DateOnly OccurredOn { get; set; }

    /// <summary>The time it happened, when it is known.</summary>
    public TimeOnly? OccurredAt { get; set; }

    /// <summary>Where exactly: "Bay 4, fell from the bedside table". Free text, and never about a patient.</summary>
    public string? Place { get; set; }

    /// <summary>What happened, in the reporter's own words.</summary>
    public required string Description { get; set; }

    /// <summary>The staff member who was handling it, when that is worth recording. Staff only.</summary>
    public string? InvolvedPerson { get; set; }

    /// <summary>What was done straight away: "Switched off and sent to the biomedical store."</summary>
    public string? ImmediateAction { get; set; }

    /// <summary>Whether the machine was taken out of use because of it.</summary>
    public bool TakenOutOfUse { get; set; }

    public DamageLevel Damage { get; set; } = DamageLevel.None;

    public IncidentStatus Status { get; set; } = IncidentStatus.Reported;

    /// <summary>What the biomedical team found caused it. Required to close.</summary>
    public string? Findings { get; set; }

    /// <summary>What was done, or will be, so it does not happen again: training, a stand, a guard rail.</summary>
    public string? CorrectiveAction { get; set; }

    public int ReportedByUserId { get; set; }

    public DateTime ReportedAtUtc { get; set; }

    public DateTime? ClosedAtUtc { get; set; }

    public int? ClosedByUserId { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public EquipmentAsset? Equipment { get; set; }

    public Location? Location { get; set; }

    /// <summary>What to quote and what is printed: INC-2026-00012.</summary>
    public string Reference => ReferenceFor(ReportedAtUtc, Id);

    public static string ReferenceFor(DateTime reportedAtUtc, int id) => $"INC-{reportedAtUtc.Year}-{id:D5}";
}
