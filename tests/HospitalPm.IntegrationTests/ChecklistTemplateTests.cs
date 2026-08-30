using HospitalPm.Domain.Checklists;
using HospitalPm.Infrastructure.Checklists;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HospitalPm.IntegrationTests;

[Collection(nameof(PostgresCollection))]
public sealed class ChecklistTemplateTests(PostgresFixture fixture)
{
    // ---------------- the guarantee ----------------

    [Fact]
    public async Task A_published_definition_cannot_be_edited()
    {
        await using var db = fixture.CreateContext();
        var (_, version) = await PublishedAsync(db);

        version.Definition.Sections[0].Items[0].Label = "Silently rewritten";

        // This is the rule the whole design exists to protect. A PM completed
        // in 2026 must render as the technician saw it when an auditor opens
        // it in 2031.
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        var pg = Assert.IsType<PostgresException>(ex.InnerException);

        Assert.Contains("cannot be changed", pg.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_published_definition_cannot_be_edited_by_raw_sql_either()
    {
        await using var db = fixture.CreateContext();
        var (_, version) = await PublishedAsync(db);

        // The guarantee has to hold against an admin at a psql prompt, not
        // just against application code that remembers to check.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            // Passed as a parameter, not inlined: ExecuteSqlRaw reads braces
            // in the SQL string as parameter placeholders, and JSON is braces.
            db.Database.ExecuteSqlRawAsync(
                "UPDATE checklist_template_version SET definition = CAST({0} AS jsonb) WHERE id = {1}",
                "{\"sections\":[]}",
                version.Id));

        Assert.Contains("cannot be changed", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_published_version_cannot_be_deleted()
    {
        await using var db = fixture.CreateContext();
        var (_, version) = await PublishedAsync(db);

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "DELETE FROM checklist_template_version WHERE id = {0}", version.Id));

        Assert.Contains("cannot be deleted", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_published_version_cannot_return_to_draft()
    {
        await using var db = fixture.CreateContext();
        var (_, version) = await PublishedAsync(db);

        // Reopening a published version for editing is the same rewrite by
        // another route.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE checklist_template_version SET status = 10 WHERE id = {0}", version.Id));

        Assert.Contains("earlier status", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_published_version_may_still_be_archived()
    {
        await using var db = fixture.CreateContext();
        var (_, version) = await PublishedAsync(db);

        // Superseding a version is not editing it, so this must be allowed.
        version.Status = ChecklistVersionStatus.Archived;
        await db.SaveChangesAsync();

        var reloaded = await db.ChecklistTemplateVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id);
        Assert.Equal(ChecklistVersionStatus.Archived, reloaded.Status);
    }

    [Fact]
    public async Task A_draft_can_be_edited_freely()
    {
        await using var db = fixture.CreateContext();
        var template = await NewTemplateAsync(db);

        var draft = new ChecklistTemplateVersion
        {
            ChecklistTemplateId = template.Id,
            Status = ChecklistVersionStatus.Draft,
            Definition = Definition("first_check", "First wording"),
        };
        db.ChecklistTemplateVersions.Add(draft);
        await db.SaveChangesAsync();

        draft.Definition = Definition("first_check", "Second wording");
        await db.SaveChangesAsync();

        var reloaded = await db.ChecklistTemplateVersions.AsNoTracking().SingleAsync(v => v.Id == draft.Id);
        Assert.Equal("Second wording", reloaded.Definition.Sections[0].Items[0].Label);
    }

    // ---------------- version bookkeeping ----------------

    [Fact]
    public async Task A_template_cannot_have_two_published_versions()
    {
        await using var db = fixture.CreateContext();
        var (template, _) = await PublishedAsync(db);

        db.ChecklistTemplateVersions.Add(new ChecklistTemplateVersion
        {
            ChecklistTemplateId = template.Id,
            VersionNo = 2,
            Status = ChecklistVersionStatus.Published,
            Definition = Definition("other", "Other"),
            PublishedAtUtc = DateTime.UtcNow,
        });

        // Two published versions would make "which questions apply now"
        // ambiguous.
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_template_cannot_have_two_drafts()
    {
        await using var db = fixture.CreateContext();
        var template = await NewTemplateAsync(db);

        db.ChecklistTemplateVersions.Add(new ChecklistTemplateVersion
        {
            ChecklistTemplateId = template.Id,
            Status = ChecklistVersionStatus.Draft,
            Definition = new ChecklistDefinition(),
        });
        await db.SaveChangesAsync();

        db.ChecklistTemplateVersions.Add(new ChecklistTemplateVersion
        {
            ChecklistTemplateId = template.Id,
            Status = ChecklistVersionStatus.Draft,
            Definition = new ChecklistDefinition(),
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task An_archived_version_still_reads_back_its_original_questions()
    {
        await using var db = fixture.CreateContext();
        var (template, v1) = await PublishedAsync(db, "flow_rate", "Measure flow at 40 L/min");

        // Supersede it the way publishing a second version would.
        v1.Status = ChecklistVersionStatus.Archived;
        await db.SaveChangesAsync();

        db.ChecklistTemplateVersions.Add(new ChecklistTemplateVersion
        {
            ChecklistTemplateId = template.Id,
            VersionNo = 2,
            Status = ChecklistVersionStatus.Published,
            Definition = Definition("flow_rate", "Measure flow at 60 L/min"),
            PublishedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var archived = await db.ChecklistTemplateVersions.AsNoTracking().SingleAsync(v => v.Id == v1.Id);

        // The old wording survives untouched. This is what an auditor opening
        // a four-year-old completion actually sees.
        Assert.Equal("Measure flow at 40 L/min", archived.Definition.Sections[0].Items[0].Label);
    }

    [Fact]
    public async Task Definition_round_trips_through_jsonb_with_all_item_types()
    {
        await using var db = fixture.CreateContext();
        var template = await NewTemplateAsync(db);

        var definition = new ChecklistDefinition
        {
            Sections =
            [
                new ChecklistSection
                {
                    Title = "Mixed",
                    Items =
                    [
                        new ChecklistItem { Key = "casing", Label = "Casing intact", Type = ChecklistItemType.PassFail },
                        new ChecklistItem { Key = "powered", Label = "Powers on", Type = ChecklistItemType.YesNo },
                        new ChecklistItem
                        {
                            Key = "flow", Label = "Flow", Type = ChecklistItemType.Number,
                            Unit = "L/min", Min = 10.5m, Max = 60m, Guidance = "Read at the outlet",
                        },
                        new ChecklistItem { Key = "notes", Label = "Observations", Type = ChecklistItemType.Text, Required = false },
                        new ChecklistItem
                        {
                            Key = "filter", Label = "Filter condition", Type = ChecklistItemType.Choice,
                            Options = ["Clean", "Soiled", "Replaced"],
                        },
                    ],
                },
            ],
        };

        db.ChecklistTemplateVersions.Add(new ChecklistTemplateVersion
        {
            ChecklistTemplateId = template.Id,
            Status = ChecklistVersionStatus.Draft,
            Definition = definition,
        });
        await db.SaveChangesAsync();

        var reloaded = await db.ChecklistTemplateVersions.AsNoTracking()
            .SingleAsync(v => v.ChecklistTemplateId == template.Id);

        var items = reloaded.Definition.Sections[0].Items;
        Assert.Equal(5, items.Count);
        Assert.Equal(10.5m, items[2].Min);
        Assert.Equal("L/min", items[2].Unit);
        Assert.Equal("Read at the outlet", items[2].Guidance);
        Assert.False(items[3].Required);
        Assert.Equal(["Clean", "Soiled", "Replaced"], items[4].Options);
    }

    [Fact]
    public async Task Checklist_changes_are_audited()
    {
        await using var db = fixture.CreateContext();
        var template = await NewTemplateAsync(db);

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open) await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE table_name = 'checklist_template' AND record_pk = @pk", conn);
        cmd.Parameters.AddWithValue("pk", template.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(
            Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0);
    }

    // ---------------- helpers ----------------

    private static ChecklistDefinition Definition(string key, string label) => new()
    {
        Sections =
        [
            new ChecklistSection
            {
                Title = "Checks",
                Items = [new ChecklistItem { Key = key, Label = label, Type = ChecklistItemType.PassFail }],
            },
        ],
    };

    private static async Task<ChecklistTemplate> NewTemplateAsync(HospitalPmDbContext db)
    {
        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");

        var template = new ChecklistTemplate
        {
            EquipmentTypeId = type.Id,
            Code = "CL-" + Guid.NewGuid().ToString("N")[..10],
            Name = "Quarterly PM",
        };

        db.ChecklistTemplates.Add(template);
        await db.SaveChangesAsync();
        return template;
    }

    private static async Task<(ChecklistTemplate Template, ChecklistTemplateVersion Version)> PublishedAsync(
        HospitalPmDbContext db, string key = "casing", string label = "Casing intact")
    {
        var template = await NewTemplateAsync(db);

        var version = new ChecklistTemplateVersion
        {
            ChecklistTemplateId = template.Id,
            VersionNo = 1,
            Status = ChecklistVersionStatus.Published,
            Definition = Definition(key, label),
            PublishedAtUtc = DateTime.UtcNow,
        };

        db.ChecklistTemplateVersions.Add(version);
        await db.SaveChangesAsync();

        return (template, version);
    }
}
