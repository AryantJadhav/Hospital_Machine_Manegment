using System.Globalization;
using System.Text;
using HospitalPm.Domain.Training;
using HospitalPm.Infrastructure.Export;

namespace HospitalPm.Infrastructure.Reports;

public sealed record TrainingAttendeeInput(int? UserId, string Name, string? Designation);

/// <summary>One training session, as the endpoint reads it.</summary>
public sealed record TrainingSessionInput(
    int Id,
    string Title,
    DateOnly Date,
    string? EquipmentType,
    int? EquipmentId,
    string? AssetTag,
    string? TrainerType,
    string? Trainer,
    string? Venue,
    int? DurationMinutes,
    IReadOnlyList<TrainingAttendeeInput> Attendees);

public sealed record TrainingSessionLine(
    int Id,
    string Title,
    DateOnly Date,
    bool Planned,
    string? EquipmentType,
    int? EquipmentId,
    string? AssetTag,
    string? TrainerType,
    string? Trainer,
    string? Venue,
    int? DurationMinutes,
    int Attendees,
    IReadOnlyList<string> AttendeeNames);

/// <summary>One kind of machine (or "General") and how much training it had in the period.</summary>
public sealed record TrainingByType(string EquipmentType, int Sessions, int Attendances);

/// <summary>One person and what they attended in the period.</summary>
public sealed record TrainingPerson(int? UserId, string Name, string? Designation, int Sessions, double Hours);

public sealed record TrainingSummaryData(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<TrainingSessionLine> Sessions,
    IReadOnlyList<TrainingByType> ByType,
    IReadOnlyList<TrainingPerson> People)
{
    /// <summary>Sessions that have happened. A session dated after today is planned and counts for nothing yet.</summary>
    public int Held => Sessions.Count(s => !s.Planned);

    public int Planned => Sessions.Count(s => s.Planned);

    /// <summary>Places taken: a person at three sessions is three.</summary>
    public int Attendances => Sessions.Where(s => !s.Planned).Sum(s => s.Attendees);

    public int PeopleTrained => People.Count;

    /// <summary>The hours of teaching given, counting each session once.</summary>
    public double Hours => Math.Round(Sessions.Where(s => !s.Planned).Sum(s => (s.DurationMinutes ?? 0) / 60.0), 2);

    /// <summary>Hours of teaching received, counting every person present: a 2-hour session for 10 is 20.</summary>
    public double PersonHours =>
        Math.Round(Sessions.Where(s => !s.Planned).Sum(s => (s.DurationMinutes ?? 0) / 60.0 * s.Attendees), 2);

    /// <summary>Held sessions with no length recorded, so the hours above are at least that, not exactly.</summary>
    public int WithoutDuration => Sessions.Count(s => !s.Planned && s.DurationMinutes is null);
}

/// <summary>
/// The biomedical department's training in a period: how many sessions, for how many people, on what, and who has
/// been trained. What an accreditation assessor asks for.
///
/// Built from plain values with no database, so the counting can be tested directly. The people are staff:
/// engineers, nurses, technicians. There is no patient data here and none can be added.
/// </summary>
public static class TrainingSummaryReport
{
    /// <summary>What the report says for a session that is not about one kind of machine.</summary>
    public const string General = "General (not one kind of machine)";

    public static TrainingSummaryData Build(IEnumerable<TrainingSessionInput> inputs, DateOnly from, DateOnly to, DateOnly today)
    {
        var sessions = inputs
            .Select(i => new TrainingSessionLine(
                i.Id,
                i.Title,
                i.Date,
                i.Date > today,
                i.EquipmentType,
                i.EquipmentId,
                i.AssetTag,
                i.TrainerType,
                i.Trainer,
                i.Venue,
                i.DurationMinutes,
                i.Attendees.Count,
                i.Attendees.Select(a => a.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()))
            // The latest first, as the other reports list; a planned session sits above the ones already held.
            .OrderByDescending(s => s.Date)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Id)
            .ToList();

        var held = inputs.Where(i => i.Date <= today).ToList();

        var byType = held
            .GroupBy(i => string.IsNullOrWhiteSpace(i.EquipmentType) ? General : i.EquipmentType!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TrainingByType(g.Key, g.Count(), g.Sum(i => i.Attendees.Count)))
            // Most training first; the sessions not about one kind of machine last, whatever their count.
            .OrderBy(t => t.EquipmentType == General ? 1 : 0)
            .ThenByDescending(t => t.Sessions)
            .ThenBy(t => t.EquipmentType, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A person is the account when there is one, so a name spelled two ways is still one person; otherwise the
        // name, ignoring case and extra spaces.
        var people = held
            .SelectMany(i => i.Attendees.Select(a => (Session: i, Attendee: a)))
            .GroupBy(x => PersonKey(x.Attendee))
            .Select(g =>
            {
                var latest = g.OrderByDescending(x => x.Session.Date).ThenByDescending(x => x.Session.Id).First();
                return new TrainingPerson(
                    g.Select(x => x.Attendee.UserId).FirstOrDefault(id => id is not null),
                    latest.Attendee.Name,
                    g.OrderByDescending(x => x.Session.Date).Select(x => x.Attendee.Designation)
                        .FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)),
                    // A person listed twice on one session is present once.
                    g.Select(x => x.Session.Id).Distinct().Count(),
                    Math.Round(g.GroupBy(x => x.Session.Id).Sum(s => (s.First().Session.DurationMinutes ?? 0) / 60.0), 2));
            })
            .OrderByDescending(p => p.Sessions)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new TrainingSummaryData(from, to, sessions, byType, people);
    }

    private static string PersonKey(TrainingAttendeeInput a) =>
        a.UserId is { } id
            ? $"u{id}"
            : "n" + string.Join(' ', a.Name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    public static string ToCsv(TrainingSummaryData report)
    {
        var sb = new StringBuilder();
        sb.Append('﻿'); // so Excel reads names in UTF-8
        sb.Append("Date,Session,Status,Kind of machine,Machine,Trainer from,Trainer,Venue,Minutes,People,Who came\r\n");

        foreach (var s in report.Sessions)
        {
            sb.Append(Csv.Line(
            [
                s.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                s.Title,
                s.Planned ? "Planned" : "Held",
                s.EquipmentType ?? string.Empty,
                s.AssetTag ?? string.Empty,
                TrainerKind.Label(s.TrainerType) ?? string.Empty,
                s.Trainer ?? string.Empty,
                s.Venue ?? string.Empty,
                s.DurationMinutes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                s.Attendees.ToString(CultureInfo.InvariantCulture),
                string.Join("; ", s.AttendeeNames),
            ])).Append("\r\n");
        }

        return sb.ToString();
    }
}
