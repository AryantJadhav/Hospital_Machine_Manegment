using System.Globalization;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Infrastructure.Checklists;

namespace HospitalPm.Infrastructure.Maintenance;

public sealed record AnswerValidationResult(
    IReadOnlyList<ValidationProblem> Problems,
    IReadOnlyDictionary<string, ChecklistAnswer> Normalised)
{
    public bool IsValid => Problems.Count == 0;
}

/// <summary>
/// Checks a submitted checklist against the version it was filled under.
///
/// The guiding rule: reject what cannot be interpreted, record what can.
/// A reading outside its acceptable range is a finding, not an error —
/// refusing to store an out-of-spec measurement is how real data quietly
/// gets rounded back into range.
/// </summary>
public static class ChecklistAnswerValidator
{
    private static readonly string[] PassFailValues = ["pass", "fail", "na"];
    private static readonly string[] YesNoValues = ["yes", "no", "na"];

    public static AnswerValidationResult Validate(
        ChecklistDefinition definition,
        IDictionary<string, ChecklistAnswer> answers)
    {
        var problems = new List<ValidationProblem>();
        var normalised = new Dictionary<string, ChecklistAnswer>(StringComparer.Ordinal);

        var items = definition.Sections
            .SelectMany(s => s.Items)
            .ToDictionary(i => i.Key, StringComparer.Ordinal);

        // An answer whose key is not in the checklist cannot be rendered
        // against anything, so it would be silently invisible forever. Almost
        // always a client sending a stale definition.
        foreach (var key in answers.Keys.Where(k => !items.ContainsKey(k)))
        {
            problems.Add(new ValidationProblem(
                key,
                $"'{key}' is not part of this checklist version. The app may be showing an outdated copy."));
        }

        foreach (var (key, item) in items)
        {
            answers.TryGetValue(key, out var answer);
            var given = answer?.Value?.Trim();

            if (string.IsNullOrWhiteSpace(given))
            {
                if (item.Required)
                {
                    problems.Add(new ValidationProblem(key, $"'{item.Label}' is required."));
                }
                continue;
            }

            var accepted = ValidateValue(item, given, problems);
            if (accepted is null)
            {
                continue;
            }

            normalised[key] = new ChecklistAnswer
            {
                Value = accepted.Value.Value,
                Note = string.IsNullOrWhiteSpace(answer?.Note) ? null : answer.Note.Trim(),
                OutOfRange = accepted.Value.OutOfRange,
            };
        }

        return new AnswerValidationResult(problems, normalised);
    }

    private static (string Value, bool OutOfRange)? ValidateValue(
        ChecklistItem item, string given, List<ValidationProblem> problems)
    {
        switch (item.Type)
        {
            case ChecklistItemType.PassFail:
            case ChecklistItemType.YesNo:
            {
                var allowed = item.Type == ChecklistItemType.PassFail ? PassFailValues : YesNoValues;
                var lowered = given.ToLowerInvariant();

                if (!allowed.Contains(lowered, StringComparer.Ordinal))
                {
                    problems.Add(new ValidationProblem(
                        item.Key,
                        $"'{item.Label}' must be one of: {string.Join(", ", allowed)}."));
                    return null;
                }

                return (lowered, false);
            }

            case ChecklistItemType.Number:
            {
                // Invariant parsing. The device may be set to any locale, and
                // a reading that means 42.5 must not be stored as 425 because
                // a comma was used as the decimal separator.
                if (!decimal.TryParse(given, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                {
                    problems.Add(new ValidationProblem(item.Key, $"'{item.Label}' must be a number."));
                    return null;
                }

                // Recorded, then flagged. The measurement is the finding.
                var outOfRange = (item.Min is not null && value < item.Min)
                              || (item.Max is not null && value > item.Max);

                return (value.ToString(CultureInfo.InvariantCulture), outOfRange);
            }

            case ChecklistItemType.Choice:
            {
                if (item.Options is null || !item.Options.Contains(given, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add(new ValidationProblem(
                        item.Key,
                        $"'{given}' is not one of the options for '{item.Label}'."));
                    return null;
                }

                // Stored as the option was defined, not as it was sent, so a
                // casing difference does not fragment reporting.
                return (item.Options.First(o => o.Equals(given, StringComparison.OrdinalIgnoreCase)), false);
            }

            case ChecklistItemType.Text:
                return (given, false);

            default:
                problems.Add(new ValidationProblem(item.Key, $"Unsupported item type on '{item.Label}'."));
                return null;
        }
    }
}
