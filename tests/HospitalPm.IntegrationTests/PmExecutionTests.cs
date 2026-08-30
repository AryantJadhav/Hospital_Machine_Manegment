using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Infrastructure.Maintenance;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Answer validation is pure, so most of this needs no database.
/// </summary>
public sealed class ChecklistAnswerTests
{
    private static ChecklistDefinition Definition() => new()
    {
        Sections =
        [
            new ChecklistSection
            {
                Title = "Checks",
                Items =
                [
                    new ChecklistItem { Key = "casing", Label = "Casing intact", Type = ChecklistItemType.PassFail },
                    new ChecklistItem
                    {
                        Key = "flow", Label = "Flow rate", Type = ChecklistItemType.Number,
                        Unit = "L/min", Min = 36, Max = 44,
                    },
                    new ChecklistItem
                    {
                        Key = "filter", Label = "Filter", Type = ChecklistItemType.Choice,
                        Options = ["Clean", "Soiled", "Replaced"],
                    },
                    new ChecklistItem
                    {
                        Key = "notes", Label = "Observations", Type = ChecklistItemType.Text, Required = false,
                    },
                ],
            },
        ],
    };

    private static Dictionary<string, ChecklistAnswer> Answers(params (string Key, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => new ChecklistAnswer { Value = p.Value }, StringComparer.Ordinal);

    [Fact]
    public void A_complete_submission_validates()
    {
        var result = ChecklistAnswerValidator.Validate(
            Definition(), Answers(("casing", "pass"), ("flow", "40"), ("filter", "Clean")));

        Assert.True(result.IsValid);
        Assert.Equal(3, result.Normalised.Count);
    }

    [Fact]
    public void A_missing_required_item_is_rejected()
    {
        var result = ChecklistAnswerValidator.Validate(
            Definition(), Answers(("casing", "pass"), ("filter", "Clean")));

        Assert.Contains(result.Problems, p => p.Path == "flow");
    }

    [Fact]
    public void An_optional_item_may_be_left_blank()
    {
        var result = ChecklistAnswerValidator.Validate(
            Definition(), Answers(("casing", "pass"), ("flow", "40"), ("filter", "Clean")));

        Assert.True(result.IsValid);
        Assert.DoesNotContain("notes", result.Normalised.Keys);
    }

    [Fact]
    public void An_out_of_range_reading_is_recorded_and_flagged_not_rejected()
    {
        var result = ChecklistAnswerValidator.Validate(
            Definition(), Answers(("casing", "pass"), ("flow", "12"), ("filter", "Clean")));

        // The measurement is the finding. Rejecting it is how real data gets
        // rounded back into range.
        Assert.True(result.IsValid);
        Assert.Equal("12", result.Normalised["flow"].Value);
        Assert.True(result.Normalised["flow"].OutOfRange);
    }

    [Fact]
    public void An_in_range_reading_is_not_flagged()
    {
        var result = ChecklistAnswerValidator.Validate(
            Definition(), Answers(("casing", "pass"), ("flow", "44"), ("filter", "Clean")));

        Assert.False(result.Normalised["flow"].OutOfRange);
    }

    [Fact]
    public void Numbers_are_parsed_invariant_regardless_of_device_locale()
    {
        // A device set to a comma-decimal locale must not turn 42.5 into 425.
        var ok = ChecklistAnswerValidator.Validate(
            Definition(), Answers(("casing", "pass"), ("flow", "42.5"), ("filter", "Clean")));

        Assert.True(ok.IsValid);
        Assert.Equal("42.5", ok.Normalised["flow"].Value);

        var comma = ChecklistAnswerValidator.Validate(
            Definition(), Answers(("casing", "pass"), ("flow", "42,5"), ("filter", "Clean")));

        // Rejected rather than silently reinterpreted as 425, which would be
        // an order of magnitude wrong and look perfectly plausible.
        Assert.Contains(comma.Problems, p => p.Path == "flow");
    }

    [Fact]
    public void A_non_numeric_reading_is_rejected()
    {
        var result = ChecklistAnswerValidator.Validate(
            Definition(), Answers(("casing", "pass"), ("flow", "about forty"), ("filter", "Clean")));

        Assert.Contains(result.Problems, p => p.Path == "flow");
    }

    [Fact]
    public void Pass_fail_accepts_only_its_own_values()
    {
        var good = ChecklistAnswerValidator.Validate(
            Definition(), Answers(("casing", "PASS"), ("flow", "40"), ("filter", "Clean")));

        Assert.True(good.IsValid);
        Assert.Equal("pass", good.Normalised["casing"].Value);

        var bad = ChecklistAnswerValidator.Validate(
            Definition(), Answers(("casing", "maybe"), ("flow", "40"), ("filter", "Clean")));

        Assert.Contains(bad.Problems, p => p.Path == "casing");
    }

    [Fact]
    public void Not_applicable_is_a_valid_answer()
    {
        var result = ChecklistAnswerValidator.Validate(
            Definition(), Answers(("casing", "na"), ("flow", "40"), ("filter", "Clean")));

        // A check that genuinely does not apply to this machine must be
        // recordable, or technicians invent an answer.
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Choice_answers_are_stored_as_defined_not_as_typed()
    {
        var result = ChecklistAnswerValidator.Validate(
            Definition(), Answers(("casing", "pass"), ("flow", "40"), ("filter", "sOiLeD")));

        // Otherwise casing differences fragment every report that groups by
        // this value.
        Assert.Equal("Soiled", result.Normalised["filter"].Value);
    }

    [Fact]
    public void An_answer_not_in_the_checklist_is_rejected()
    {
        var result = ChecklistAnswerValidator.Validate(
            Definition(),
            Answers(("casing", "pass"), ("flow", "40"), ("filter", "Clean"), ("removed_in_v2", "pass")));

        // Almost always a device holding a stale copy. Storing it would leave
        // an answer that can never be rendered against anything.
        Assert.Contains(result.Problems, p => p.Path == "removed_in_v2");
    }
}

[Collection(nameof(PostgresCollection))]
public sealed class PmCompletionTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_completion_cannot_be_edited()
    {
        await using var db = fixture.CreateContext();
        var completion = await SeedCompletionAsync(db);

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE pm_completion SET signed_by_name = 'Someone else' WHERE id = {0}", completion.Id));

        Assert.Contains("cannot be", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_completion_cannot_have_its_answers_rewritten()
    {
        await using var db = fixture.CreateContext();
        var completion = await SeedCompletionAsync(db);

        // Changing a failed reading to a passing one after the fact is the
        // exact scenario this table exists to prevent.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "UPDATE pm_completion SET answers = CAST({0} AS jsonb) WHERE id = {1}",
                "{}",
                completion.Id));

        Assert.Contains("cannot be", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_completion_cannot_be_deleted()
    {
        await using var db = fixture.CreateContext();
        var completion = await SeedCompletionAsync(db);

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("DELETE FROM pm_completion WHERE id = {0}", completion.Id));

        Assert.Contains("cannot be", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Answers_round_trip_through_jsonb()
    {
        await using var db = fixture.CreateContext();
        var completion = await SeedCompletionAsync(db);

        var reloaded = await db.PmCompletions.AsNoTracking()
            .SingleAsync(c => c.Id == completion.Id);

        Assert.Equal("pass", reloaded.Answers["casing"].Value);
        Assert.True(reloaded.Answers["flow"].OutOfRange);
        Assert.Equal("Reading low, escalated", reloaded.Answers["flow"].Note);
        Assert.Equal(1, reloaded.OutOfRangeCount);
    }

    [Fact]
    public async Task A_task_can_only_have_one_completion()
    {
        await using var db = fixture.CreateContext();
        var completion = await SeedCompletionAsync(db);

        // Raw SQL deliberately: through the change tracker EF severs the
        // existing 1:1 association client-side and throws before the database
        // is asked, which would test EF rather than the constraint.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "INSERT INTO pm_completion (tenant_id, pm_task_id, checklist_template_version_id, " +
                "completed_by_user_id, completed_at_utc, answers) " +
                "VALUES (1, {0}, {1}, {2}, now(), CAST({3} AS jsonb))",
                completion.PmTaskId,
                completion.ChecklistTemplateVersionId,
                completion.CompletedByUserId,
                "{}"));

        Assert.Equal("23505", ex.SqlState); // unique_violation
    }

    [Fact]
    public async Task A_replayed_submission_id_is_rejected_at_the_database()
    {
        await using var db = fixture.CreateContext();
        var completion = await SeedCompletionAsync(db, Guid.NewGuid());
        var other = await SeedTaskAsync(db);

        // The endpoint returns the original result on replay; this is the
        // backstop if two devices ever generated the same id.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                "INSERT INTO pm_completion (tenant_id, pm_task_id, checklist_template_version_id, " +
                "completed_by_user_id, completed_at_utc, answers, client_submission_id) " +
                "VALUES (1, {0}, {1}, {2}, now(), CAST({3} AS jsonb), {4})",
                other.Task.Id,
                other.VersionId,
                other.UserId,
                "{}",
                completion.ClientSubmissionId!.Value));

        Assert.Equal("23505", ex.SqlState);
    }

    // ---------------- helpers ----------------

    private sealed record Seeded(PmTask Task, int VersionId, int UserId);

    private static async Task<Seeded> SeedTaskAsync(Infrastructure.Persistence.HospitalPmDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var room = new Domain.Locations.Location
        {
            Code = $"EXR-{suffix}", Name = $"Room {suffix}", Level = Domain.Locations.LocationLevel.Room,
        };
        db.Locations.Add(room);
        await db.SaveChangesAsync();

        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");

        var equipment = new Domain.Assets.Equipment
        {
            AssetTag = $"EX-{suffix}".ToUpperInvariant(),
            EquipmentTypeId = type.Id,
            LocationId = room.Id,
        };
        db.Equipment.Add(equipment);

        var template = new ChecklistTemplate
        {
            EquipmentTypeId = type.Id,
            Code = "EXCL-" + suffix,
            Name = "Execution test checklist",
        };
        db.ChecklistTemplates.Add(template);
        await db.SaveChangesAsync();

        var version = new ChecklistTemplateVersion
        {
            ChecklistTemplateId = template.Id,
            VersionNo = 1,
            Status = ChecklistVersionStatus.Published,
            PublishedAtUtc = DateTime.UtcNow,
            Definition = new ChecklistDefinition
            {
                Sections =
                [
                    new ChecklistSection
                    {
                        Title = "Checks",
                        Items =
                        [
                            new ChecklistItem { Key = "casing", Label = "Casing", Type = ChecklistItemType.PassFail },
                            new ChecklistItem
                            {
                                Key = "flow", Label = "Flow", Type = ChecklistItemType.Number, Min = 36, Max = 44,
                            },
                        ],
                    },
                ],
            },
        };
        db.ChecklistTemplateVersions.Add(version);

        var schedule = new PmSchedule
        {
            EquipmentId = equipment.Id,
            ChecklistTemplateId = template.Id,
            Frequency = PmFrequency.Quarterly,
            AnchorDate = new DateOnly(2026, 1, 1),
        };
        db.PmSchedules.Add(schedule);

        var user = new Infrastructure.Identity.ApplicationUser
        {
            UserName = "ex-" + suffix,
            FullName = "Execution Test Engineer",
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var task = new PmTask
        {
            PmScheduleId = schedule.Id,
            EquipmentId = equipment.Id,
            DueDate = new DateOnly(2026, 1, 1),
            Status = PmTaskStatus.Due,
        };
        db.PmTasks.Add(task);
        await db.SaveChangesAsync();

        return new Seeded(task, version.Id, user.Id);
    }

    private static async Task<PmCompletion> SeedCompletionAsync(
        Infrastructure.Persistence.HospitalPmDbContext db, Guid? submissionId = null)
    {
        var seeded = await SeedTaskAsync(db);

        var completion = new PmCompletion
        {
            PmTaskId = seeded.Task.Id,
            ChecklistTemplateVersionId = seeded.VersionId,
            CompletedByUserId = seeded.UserId,
            CompletedAtUtc = DateTime.UtcNow,
            PerformedAtUtc = DateTime.UtcNow.AddMinutes(-30),
            SignedByName = "A Technician",
            ClientSubmissionId = submissionId,
            Answers = new Dictionary<string, ChecklistAnswer>(StringComparer.Ordinal)
            {
                ["casing"] = new() { Value = "pass" },
                ["flow"] = new() { Value = "12", OutOfRange = true, Note = "Reading low, escalated" },
            },
        };

        db.PmCompletions.Add(completion);

        seeded.Task.Status = PmTaskStatus.Completed;
        seeded.Task.CompletedAtUtc = completion.CompletedAtUtc;
        seeded.Task.CompletedByUserId = seeded.UserId;
        seeded.Task.ChecklistTemplateVersionId = seeded.VersionId;

        await db.SaveChangesAsync();

        return completion;
    }
}
