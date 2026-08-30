using HospitalPm.Domain.Checklists;
using HospitalPm.Infrastructure.Checklists;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Publishing is irreversible, so these are the last chance to catch a
/// definition that cannot be corrected afterwards.
/// </summary>
public sealed class ChecklistValidatorTests
{
    private static ChecklistDefinition Valid() => new()
    {
        Sections =
        [
            new ChecklistSection
            {
                Title = "Visual inspection",
                Items =
                [
                    new ChecklistItem { Key = "casing", Label = "Casing intact", Type = ChecklistItemType.PassFail },
                    new ChecklistItem
                    {
                        Key = "flow", Label = "Flow rate", Type = ChecklistItemType.Number,
                        Unit = "L/min", Min = 10, Max = 60,
                    },
                ],
            },
        ],
    };

    [Fact]
    public void A_well_formed_checklist_passes()
        => Assert.Empty(ChecklistDefinitionValidator.Validate(Valid()));

    [Fact]
    public void An_empty_checklist_is_rejected()
    {
        var problems = ChecklistDefinitionValidator.Validate(new ChecklistDefinition());

        Assert.Contains(problems, p => p.Path == "sections");
    }

    [Fact]
    public void Duplicate_keys_are_rejected_across_sections()
    {
        var definition = Valid();
        definition.Sections.Add(new ChecklistSection
        {
            Title = "Electrical",
            // Same key as the first section's item. Answers are stored by
            // key, so one would overwrite the other's result.
            Items = [new ChecklistItem { Key = "casing", Label = "Different question", Type = ChecklistItemType.PassFail }],
        });

        var problems = ChecklistDefinitionValidator.Validate(definition);

        Assert.Contains(problems, p => p.Message.Contains("already used", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Casing")]      // uppercase
    [InlineData("1casing")]     // leading digit
    [InlineData("casing-x")]    // hyphen
    [InlineData("casing x")]    // space
    [InlineData("")]            // blank
    public void Invalid_keys_are_rejected(string key)
    {
        var definition = Valid();
        definition.Sections[0].Items[0].Key = key;

        var problems = ChecklistDefinitionValidator.Validate(definition);

        Assert.Contains(problems, p => p.Path.EndsWith(".key", StringComparison.Ordinal));
    }

    [Fact]
    public void An_impossible_numeric_range_is_rejected()
    {
        var definition = Valid();
        definition.Sections[0].Items[1].Min = 100;
        definition.Sections[0].Items[1].Max = 10;

        var problems = ChecklistDefinitionValidator.Validate(definition);

        // No reading could ever pass, so the checklist would fail forever and
        // nobody would know why.
        Assert.Contains(problems, p => p.Message.Contains("no reading could ever pass", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_range_on_a_pass_fail_item_is_flagged_as_a_probable_wrong_type()
    {
        var definition = Valid();
        definition.Sections[0].Items[0].Min = 1;

        var problems = ChecklistDefinitionValidator.Validate(definition);

        Assert.Contains(problems, p => p.Message.Contains("Did you mean", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_choice_item_needs_at_least_two_distinct_options()
    {
        var one = Valid();
        one.Sections[0].Items[0] = new ChecklistItem
        {
            Key = "filter", Label = "Filter", Type = ChecklistItemType.Choice, Options = ["Clean"],
        };

        var dupes = Valid();
        dupes.Sections[0].Items[0] = new ChecklistItem
        {
            Key = "filter", Label = "Filter", Type = ChecklistItemType.Choice, Options = ["Clean", "clean"],
        };

        Assert.Contains(ChecklistDefinitionValidator.Validate(one), p => p.Path.EndsWith(".options", StringComparison.Ordinal));
        Assert.Contains(ChecklistDefinitionValidator.Validate(dupes), p => p.Path.EndsWith(".options", StringComparison.Ordinal));
    }

    [Fact]
    public void A_section_with_no_items_is_rejected()
    {
        var definition = Valid();
        definition.Sections.Add(new ChecklistSection { Title = "Empty", Items = [] });

        var problems = ChecklistDefinitionValidator.Validate(definition);

        Assert.Contains(problems, p => p.Message.Contains("no items", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Problems_carry_a_path_the_editor_can_point_at()
    {
        var definition = Valid();
        definition.Sections[0].Items[1].Label = "";

        var problem = Assert.Single(ChecklistDefinitionValidator.Validate(definition));

        // The author needs to be shown which item is wrong, not just told
        // that something is.
        Assert.Equal("sections[0].items[1].label", problem.Path);
    }
}
