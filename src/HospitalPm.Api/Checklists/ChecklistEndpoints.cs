using HospitalPm.Api.Auth;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Checklists;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// See EquipmentEndpoints: EF Core cannot translate the StringComparison
// overloads CA1862 recommends.
#pragma warning disable CA1862

namespace HospitalPm.Api.Checklists;

public sealed record TemplateRequest(
    int EquipmentTypeId, string Code, string Name, string? Description, ChecklistKind? Kind = null);

public sealed record DefinitionRequest(ChecklistDefinition Definition, string? ChangeNote);

public sealed record TemplateResponse(
    int Id,
    int EquipmentTypeId,
    string EquipmentTypeName,
    string Code,
    string Name,
    string? Description,
    bool IsActive,
    int? PublishedVersionNo,
    bool HasDraft,
    int VersionCount,
    ChecklistKind Kind);

public sealed record VersionResponse(
    int Id,
    int VersionNo,
    ChecklistVersionStatus Status,
    ChecklistDefinition Definition,
    string? ChangeNote,
    DateTime? PublishedAtUtc,
    int ItemCount);

public static class ChecklistEndpoints
{
    public static void MapChecklistEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/checklists")
            .WithTags("Checklists")
            .RequirePermission(Permissions.ChecklistsView);

        // Every role reads them: a technician needs to see what they will be
        // asked before they walk to the machine.
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapGet("/{id:int}/versions", VersionsAsync);
        group.MapGet("/{id:int}/published", PublishedAsync);

        // Authoring is a register-owner job. A checklist defines what counts
        // as a completed PM, which is evidence in an audit.
        var authoring = group.MapGroup(string.Empty)
            .RequirePermission(Permissions.ChecklistsEdit);

        authoring.MapPost("/", CreateAsync);
        authoring.MapPut("/{id:int}/draft", SaveDraftAsync);
        authoring.MapPost("/{id:int}/validate", ValidateDraftAsync);
        authoring.MapPost("/{id:int}/publish", PublishAsync);
    }

    private static async Task<IResult> ListAsync(
        HospitalPmDbContext db, [FromQuery] int? equipmentTypeId, [FromQuery] ChecklistKind? kind, CancellationToken ct)
    {
        // Only PM checklists. The daily check that used a second kind is gone, and any that were
        // written for it are not listed.
        var query = db.ChecklistTemplates.AsNoTracking().Where(t => t.Kind == ChecklistKind.Pm);

        if (kind is not null)
        {
            query = query.Where(t => t.Kind == kind);
        }

        if (equipmentTypeId is not null)
        {
            query = query.Where(t => t.EquipmentTypeId == equipmentTypeId);
        }

        var items = await query
            .OrderBy(t => t.Name)
            .Select(t => new TemplateResponse(
                t.Id,
                t.EquipmentTypeId,
                t.EquipmentType!.Name,
                t.Code,
                t.Name,
                t.Description,
                t.IsActive,
                t.Versions
                    .Where(v => v.Status == ChecklistVersionStatus.Published)
                    .Select(v => (int?)v.VersionNo)
                    .FirstOrDefault(),
                t.Versions.Any(v => v.Status == ChecklistVersionStatus.Draft),
                t.Versions.Count,
                t.Kind))
            .ToListAsync(ct);

        return Results.Ok(items);
    }

    private static async Task<IResult> GetAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        var item = await db.ChecklistTemplates.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new TemplateResponse(
                t.Id,
                t.EquipmentTypeId,
                t.EquipmentType!.Name,
                t.Code,
                t.Name,
                t.Description,
                t.IsActive,
                t.Versions
                    .Where(v => v.Status == ChecklistVersionStatus.Published)
                    .Select(v => (int?)v.VersionNo)
                    .FirstOrDefault(),
                t.Versions.Any(v => v.Status == ChecklistVersionStatus.Draft),
                t.Versions.Count,
                t.Kind))
            .SingleOrDefaultAsync(ct);

        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    /// <summary>
    /// Every version, newest first. Archived ones are included deliberately:
    /// they are what completed checklists were filled under, so an auditor
    /// needs to be able to read them.
    /// </summary>
    private static async Task<IResult> VersionsAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        if (!await db.ChecklistTemplates.AnyAsync(t => t.Id == id, ct))
        {
            return Results.NotFound();
        }

        var versions = await db.ChecklistTemplateVersions.AsNoTracking()
            .Where(v => v.ChecklistTemplateId == id)
            .OrderByDescending(v => v.Status == ChecklistVersionStatus.Draft)
            .ThenByDescending(v => v.VersionNo)
            .ToListAsync(ct);

        return Results.Ok(versions.Select(Project));
    }

    /// <summary>The version a PM done today would be recorded against.</summary>
    private static async Task<IResult> PublishedAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        var version = await db.ChecklistTemplateVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.ChecklistTemplateId == id && v.Status == ChecklistVersionStatus.Published, ct);

        return version is null
            ? Results.NotFound(new { error = "This checklist has no published version yet." })
            : Results.Ok(Project(version));
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] TemplateRequest request, HospitalPmDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = "Code and name are required." });
        }

        if (request.Kind is { } kind && !Enum.IsDefined(kind))
        {
            return Results.BadRequest(new { error = "Unknown kind of checklist." });
        }

        if (!await db.EquipmentTypes.AnyAsync(t => t.Id == request.EquipmentTypeId, ct))
        {
            return Results.BadRequest(new { error = "Unknown equipment type." });
        }

        var code = request.Code.Trim();
        if (await db.ChecklistTemplates.AnyAsync(t => t.Code.ToLower() == code.ToLower(), ct))
        {
            return Results.Conflict(new { error = $"Code '{code}' is already used by another checklist." });
        }

        var template = new ChecklistTemplate
        {
            EquipmentTypeId = request.EquipmentTypeId,
            Kind = request.Kind ?? ChecklistKind.Pm,
            Code = code,
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
        };

        db.ChecklistTemplates.Add(template);
        await db.SaveChangesAsync(ct);

        // A template with no draft cannot be edited into anything, so one is
        // created alongside it rather than making the caller ask twice.
        db.ChecklistTemplateVersions.Add(new ChecklistTemplateVersion
        {
            ChecklistTemplateId = template.Id,
            VersionNo = 0,
            Status = ChecklistVersionStatus.Draft,
            Definition = new ChecklistDefinition(),
        });
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/checklists/{template.Id}", new { template.Id });
    }

    /// <summary>
    /// Replaces the draft's content. Not validated: half-finished work is the
    /// entire point of a draft, and refusing to save it would push authors
    /// into keeping checklists in a Word document until they are perfect.
    /// </summary>
    private static async Task<IResult> SaveDraftAsync(
        int id, [FromBody] DefinitionRequest request, HospitalPmDbContext db, CancellationToken ct)
    {
        var draft = await db.ChecklistTemplateVersions
            .SingleOrDefaultAsync(v => v.ChecklistTemplateId == id && v.Status == ChecklistVersionStatus.Draft, ct);

        if (draft is null)
        {
            // Every published version is frozen, so a template whose draft was
            // published needs a fresh one to carry the next round of edits.
            if (!await db.ChecklistTemplates.AnyAsync(t => t.Id == id, ct))
            {
                return Results.NotFound();
            }

            draft = new ChecklistTemplateVersion
            {
                ChecklistTemplateId = id,
                VersionNo = 0,
                Status = ChecklistVersionStatus.Draft,
            };
            db.ChecklistTemplateVersions.Add(draft);
        }

        draft.Definition = request.Definition ?? new ChecklistDefinition();
        draft.ChangeNote = request.ChangeNote?.Trim();

        await db.SaveChangesAsync(ct);

        return Results.Ok(Project(draft));
    }

    /// <summary>
    /// Reports what publishing would reject, without publishing.
    /// </summary>
    private static async Task<IResult> ValidateDraftAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        var draft = await db.ChecklistTemplateVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.ChecklistTemplateId == id && v.Status == ChecklistVersionStatus.Draft, ct);

        if (draft is null)
        {
            return Results.NotFound(new { error = "This checklist has no draft." });
        }

        var problems = ChecklistDefinitionValidator.Validate(draft.Definition);

        return Results.Ok(new
        {
            valid = problems.Count == 0,
            problems = problems.Select(p => new { path = p.Path, message = p.Message }),
        });
    }

    /// <summary>
    /// Freezes the draft and supersedes whatever was published before.
    ///
    /// Irreversible by design. The new version becomes immutable, and the old
    /// one is archived rather than deleted because every checklist completed
    /// under it still points at it.
    /// </summary>
    private static async Task<IResult> PublishAsync(
        int id, HospitalPmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var draft = await db.ChecklistTemplateVersions
            .SingleOrDefaultAsync(v => v.ChecklistTemplateId == id && v.Status == ChecklistVersionStatus.Draft, ct);

        if (draft is null)
        {
            return Results.NotFound(new { error = "This checklist has no draft to publish." });
        }

        var problems = ChecklistDefinitionValidator.Validate(draft.Definition);
        if (problems.Count > 0)
        {
            return Results.BadRequest(new
            {
                error = "This checklist cannot be published yet.",
                problems = problems.Select(p => new { path = p.Path, message = p.Message }),
            });
        }

        // Serializable: two publishes racing would both see no published
        // version and both try to claim it, and the partial unique index
        // would fail the loser with a constraint name rather than a message.
        await using var tx = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable, ct);

        var current = await db.ChecklistTemplateVersions
            .SingleOrDefaultAsync(v => v.ChecklistTemplateId == id && v.Status == ChecklistVersionStatus.Published, ct);

        var nextNo = await db.ChecklistTemplateVersions
            .Where(v => v.ChecklistTemplateId == id)
            .MaxAsync(v => (int?)v.VersionNo, ct) ?? 0;

        if (current is not null)
        {
            current.Status = ChecklistVersionStatus.Archived;
        }

        draft.Status = ChecklistVersionStatus.Published;
        draft.VersionNo = nextNo + 1;
        draft.PublishedAtUtc = clock.GetUtcNow().UtcDateTime;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Results.Ok(new
        {
            versionNo = draft.VersionNo,
            supersededVersionNo = current?.VersionNo,
        });
    }

    private static VersionResponse Project(ChecklistTemplateVersion v) => new(
        v.Id,
        v.VersionNo,
        v.Status,
        v.Definition,
        v.ChangeNote,
        v.PublishedAtUtc,
        v.Definition.Sections.Sum(s => s.Items.Count));
}
