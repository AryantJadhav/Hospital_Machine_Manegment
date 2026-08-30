namespace HospitalPm.Domain.Checklists;

/// <summary>
/// What a technician is asked to check. Stored as JSONB on a version row and
/// never mutated once published.
/// </summary>
public sealed class ChecklistDefinition
{
    public List<ChecklistSection> Sections { get; set; } = [];
}

public sealed class ChecklistSection
{
    public required string Title { get; set; }

    public List<ChecklistItem> Items { get; set; } = [];
}

public sealed class ChecklistItem
{
    /// <summary>
    /// Stable machine key, unique within a version.
    ///
    /// Reusing the same key across versions of a template is how a reading
    /// stays comparable over time — "flow_rate" in v1 and v7 are the same
    /// measurement, so a hospital can trend it across four years even though
    /// the wording of the question changed twice. Renaming a key silently
    /// breaks that trend, which is why keys are validated but never
    /// auto-generated from the label.
    /// </summary>
    public required string Key { get; set; }

    /// <summary>What the technician actually reads on the phone.</summary>
    public required string Label { get; set; }

    public ChecklistItemType Type { get; set; } = ChecklistItemType.PassFail;

    public bool Required { get; set; } = true;

    /// <summary>Shown beneath the label — the "how", where it is not obvious.</summary>
    public string? Guidance { get; set; }

    // ---- Numeric items ----

    public string? Unit { get; set; }

    /// <summary>
    /// Acceptable range. A reading outside it is recorded and flagged rather
    /// than rejected: the measurement is the finding, and refusing to store
    /// an out-of-spec value is how real data gets rounded into range.
    /// </summary>
    public decimal? Min { get; set; }

    public decimal? Max { get; set; }

    // ---- Choice items ----

    public List<string>? Options { get; set; }
}

public enum ChecklistItemType
{
    /// <summary>Pass / Fail / Not applicable.</summary>
    PassFail = 10,

    YesNo = 20,

    /// <summary>A measurement, optionally with a unit and an acceptable range.</summary>
    Number = 30,

    /// <summary>Free text, for observations.</summary>
    Text = 40,

    /// <summary>One of a fixed list.</summary>
    Choice = 50,
}
