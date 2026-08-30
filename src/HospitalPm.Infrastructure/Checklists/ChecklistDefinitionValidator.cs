using System.Text.RegularExpressions;
using HospitalPm.Domain.Checklists;

namespace HospitalPm.Infrastructure.Checklists;

public sealed record ValidationProblem(string Path, string Message);

/// <summary>
/// Validates a checklist definition before it is published.
///
/// Publishing is irreversible: the version becomes immutable, and completed
/// checklists reference it forever. A definition with a duplicate key or an
/// impossible range cannot be corrected afterwards — the only remedy is a
/// new version, which leaves the broken one permanently in the history. So
/// the checks are strict at the boundary rather than forgiving.
///
/// Drafts are deliberately not validated on save. Half-finished work is the
/// entire point of a draft.
/// </summary>
public static partial class ChecklistDefinitionValidator
{
    /// <summary>
    /// Keys become column-ish identifiers in exports and trend queries, so
    /// they are restricted to something safe to use as one.
    /// </summary>
    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex KeyPattern();

    public const int MaxSections = 50;
    public const int MaxItemsPerSection = 200;

    public static IReadOnlyList<ValidationProblem> Validate(ChecklistDefinition definition)
    {
        var problems = new List<ValidationProblem>();

        if (definition is null)
        {
            return [new ValidationProblem("definition", "The checklist has no content.")];
        }

        if (definition.Sections.Count == 0)
        {
            problems.Add(new ValidationProblem("sections", "Add at least one section before publishing."));
            return problems;
        }

        if (definition.Sections.Count > MaxSections)
        {
            problems.Add(new ValidationProblem(
                "sections", $"A checklist may have at most {MaxSections} sections."));
        }

        // Keys are unique across the whole checklist, not just within a
        // section. A completion stores answers keyed by this, so two items
        // sharing a key would overwrite each other's answer.
        var seenKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var totalItems = 0;

        for (var s = 0; s < definition.Sections.Count; s++)
        {
            var section = definition.Sections[s];
            var sectionPath = $"sections[{s}]";

            if (string.IsNullOrWhiteSpace(section.Title))
            {
                problems.Add(new ValidationProblem($"{sectionPath}.title", "Section title is required."));
            }

            if (section.Items.Count == 0)
            {
                problems.Add(new ValidationProblem(
                    $"{sectionPath}.items",
                    $"Section '{section.Title}' has no items. Remove it, or add a check."));
            }

            if (section.Items.Count > MaxItemsPerSection)
            {
                problems.Add(new ValidationProblem(
                    $"{sectionPath}.items",
                    $"A section may have at most {MaxItemsPerSection} items."));
            }

            for (var i = 0; i < section.Items.Count; i++)
            {
                var item = section.Items[i];
                var itemPath = $"{sectionPath}.items[{i}]";
                totalItems++;

                ValidateItem(item, itemPath, seenKeys, problems);
            }
        }

        if (totalItems == 0)
        {
            problems.Add(new ValidationProblem("sections", "The checklist has no items."));
        }

        return problems;
    }

    private static void ValidateItem(
        ChecklistItem item,
        string path,
        Dictionary<string, string> seenKeys,
        List<ValidationProblem> problems)
    {
        if (string.IsNullOrWhiteSpace(item.Key))
        {
            problems.Add(new ValidationProblem($"{path}.key", "Item key is required."));
        }
        else if (!KeyPattern().IsMatch(item.Key))
        {
            problems.Add(new ValidationProblem(
                $"{path}.key",
                $"Key '{item.Key}' must start with a lowercase letter and contain only lowercase letters, digits and underscores."));
        }
        else if (seenKeys.TryGetValue(item.Key, out var firstPath))
        {
            problems.Add(new ValidationProblem(
                $"{path}.key",
                $"Key '{item.Key}' is already used at {firstPath}. Answers are stored by key, so duplicates would overwrite each other."));
        }
        else
        {
            seenKeys[item.Key] = path;
        }

        if (string.IsNullOrWhiteSpace(item.Label))
        {
            problems.Add(new ValidationProblem($"{path}.label", "Item label is required."));
        }

        if (!Enum.IsDefined(item.Type))
        {
            problems.Add(new ValidationProblem($"{path}.type", "Unknown item type."));
            return;
        }

        switch (item.Type)
        {
            case ChecklistItemType.Number:
                if (item.Min is not null && item.Max is not null && item.Min > item.Max)
                {
                    problems.Add(new ValidationProblem(
                        $"{path}.min",
                        $"Minimum ({item.Min}) is above maximum ({item.Max}); no reading could ever pass."));
                }
                break;

            case ChecklistItemType.Choice:
                if (item.Options is null || item.Options.Count < 2)
                {
                    problems.Add(new ValidationProblem(
                        $"{path}.options",
                        "A choice item needs at least two options."));
                }
                else if (item.Options.Any(string.IsNullOrWhiteSpace))
                {
                    problems.Add(new ValidationProblem($"{path}.options", "Options cannot be blank."));
                }
                else if (item.Options.Distinct(StringComparer.OrdinalIgnoreCase).Count() != item.Options.Count)
                {
                    problems.Add(new ValidationProblem($"{path}.options", "Options must be distinct."));
                }
                break;

            case ChecklistItemType.PassFail:
            case ChecklistItemType.YesNo:
            case ChecklistItemType.Text:
                // Ranges and options are meaningless on these. Flagged rather
                // than ignored, because a range set on a pass/fail item almost
                // always means the author picked the wrong type.
                if (item.Min is not null || item.Max is not null)
                {
                    problems.Add(new ValidationProblem(
                        $"{path}.min",
                        $"A {item.Type} item cannot have a numeric range. Did you mean to make it a Number?"));
                }
                if (item.Options is { Count: > 0 })
                {
                    problems.Add(new ValidationProblem(
                        $"{path}.options",
                        $"A {item.Type} item cannot have options. Did you mean to make it a Choice?"));
                }
                break;

            default:
                break;
        }
    }
}
