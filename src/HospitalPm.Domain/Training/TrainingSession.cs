using HospitalPm.Domain.Equipment;
using EquipmentAsset = HospitalPm.Domain.Assets.Equipment;

namespace HospitalPm.Domain.Training;

/// <summary>
/// One training session the biomedical department held or has planned: what it was about, when,
/// who ran it, and who came.
///
/// Answers "who has been trained on the ventilator, and when?" - the question an accreditation
/// assessor asks and a department otherwise answers from a paper register.
///
/// NO PATIENT DATA. The people here are staff: engineers, nurses, technicians. A session is linked
/// to an equipment type, never to a patient. See CLAUDE.md.
/// </summary>
public sealed class TrainingSession
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    /// <summary>What it was about: "Ventilator alarm handling", "Infusion pump basics".</summary>
    public required string Title { get; set; }

    /// <summary>The day it was held. A day in the future is a session that is planned.</summary>
    public DateOnly SessionDate { get; set; }

    /// <summary>
    /// The kind of machine it covered. Optional: a session on safety, or on infection control around
    /// equipment, is not about one kind.
    /// </summary>
    public int? EquipmentTypeId { get; set; }

    /// <summary>
    /// The machine the training was given on. Its name, make and model are read from the machine's own
    /// record rather than typed again, so ten machines of one model are told apart by their numbers.
    /// Optional: a session on safety is not about one machine.
    /// </summary>
    public int? EquipmentId { get; set; }

    /// <summary>Who ran it - one of our own people, or the manufacturer's applications specialist. Free text.</summary>
    public string? Trainer { get; set; }

    /// <summary>Where it was held - "ICU seminar room". Free text.</summary>
    public string? Venue { get; set; }

    public int? DurationMinutes { get; set; }

    public string? Notes { get; set; }

    public int CreatedByUserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public EquipmentType? EquipmentType { get; set; }

    public EquipmentAsset? Machine { get; set; }

    public ICollection<TrainingAttendee> Attendees { get; set; } = [];
}

/// <summary>
/// One person who attended a session.
///
/// Most of the people trained on a ward's equipment are nurses and technicians with no login, so an
/// attendee is a name first. When the person does have an account, <see cref="UserId"/> links them,
/// so the same person is recognised across sessions even if the spelling of the name differs.
/// </summary>
public sealed class TrainingAttendee
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public int TrainingSessionId { get; set; }

    /// <summary>The staff account, when they have one.</summary>
    public int? UserId { get; set; }

    /// <summary>The name as it should read on the record. Copied from the account when there is one, so the record does not change if the account is renamed.</summary>
    public required string Name { get; set; }

    /// <summary>"Staff nurse, ICU", "Biomedical engineer". Optional.</summary>
    public string? Designation { get; set; }

    public TrainingSession? Session { get; set; }
}
