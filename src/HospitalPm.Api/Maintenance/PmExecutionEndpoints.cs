using System.Security.Claims;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Maintenance;

public sealed record AnswerInput(string Value, string? Note);

public sealed record CompleteRequest(
    int ChecklistTemplateVersionId,
    Dictionary<string, AnswerInput> Answers,
    string? SignatureBase64,
    /// <summary>"png" or "svg". Defaults to png for a browser canvas.</summary>
    string? SignatureFormat,
    string? SignedByName,
    DateTime? PerformedAtUtc,
    Guid? ClientSubmissionId,
    string? Notes);

public sealed record SkipRequest(string Reason, Guid? ClientSubmissionId);

public static class PmExecutionEndpoints
{
    /// <summary>A signature drawn on a phone. A megabyte is not one.</summary>
    private const int MaxSignatureBytes = 1024 * 1024;

    public static void MapPmExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pm/tasks").WithTags("PM execution").RequireAuthorization();

        // Everything a device needs to render the checklist for one task.
        group.MapGet("/{id:int}/form", FormAsync);
        group.MapGet("/{id:int}/completion", CompletionAsync);

        // Doing the PM is the job. Anyone signed in may record one —
        // that is what an Employee account exists for.
        group.MapPost("/{id:int}/complete", CompleteAsync);

        // Deciding a PM will not happen is not. A skip is a permanent gap
        // in the maintenance record, and choosing that a machine goes
        // unmaintained this quarter is a call about the department, not a
        // call for whoever is holding the work list that morning.
        group.MapPost("/{id:int}/skip", SkipAsync)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }

    /// <summary>
    /// The task plus the checklist to fill in.
    ///
    /// Returns the published version, and tells the caller which one it is so
    /// the submission can name the version actually rendered.
    /// </summary>
    private static async Task<IResult> FormAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        var task = await db.PmTasks.AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new
            {
                t.Id,
                t.Status,
                t.DueDate,
                t.EquipmentId,
                t.Equipment!.AssetTag,
                EquipmentTypeName = t.Equipment!.EquipmentType!.Name,
                LocationName = t.Equipment!.Location!.Name,
                t.Schedule!.ChecklistTemplateId,
                ChecklistName = t.Schedule!.ChecklistTemplate!.Name,
            })
            .SingleOrDefaultAsync(ct);

        if (task is null)
        {
            return Results.NotFound();
        }

        var version = await db.ChecklistTemplateVersions.AsNoTracking()
            .SingleOrDefaultAsync(
                v => v.ChecklistTemplateId == task.ChecklistTemplateId
                     && v.Status == ChecklistVersionStatus.Published, ct);

        if (version is null)
        {
            return Results.Conflict(new
            {
                error = "This checklist has no published version, so the PM cannot be recorded yet.",
            });
        }

        return Results.Ok(new
        {
            task.Id,
            task.Status,
            task.DueDate,
            task.EquipmentId,
            task.AssetTag,
            task.EquipmentTypeName,
            task.LocationName,
            task.ChecklistName,
            checklistTemplateVersionId = version.Id,
            versionNo = version.VersionNo,
            definition = version.Definition,
        });
    }

    /// <summary>
    /// A finished PM, rendered against the version it was filled under —
    /// which is the whole point of pinning that version.
    /// </summary>
    private static async Task<IResult> CompletionAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        var completion = await db.PmCompletions.AsNoTracking()
            .SingleOrDefaultAsync(c => c.PmTaskId == id, ct);

        if (completion is null)
        {
            return Results.NotFound();
        }

        var version = await db.ChecklistTemplateVersions.AsNoTracking()
            .SingleAsync(v => v.Id == completion.ChecklistTemplateVersionId, ct);

        return Results.Ok(new
        {
            completion.Id,
            completion.PmTaskId,
            completion.CompletedAtUtc,
            completion.PerformedAtUtc,
            completion.SignedByName,
            completion.Notes,
            hasSignature = completion.Signature is not null,
            signatureFormat = completion.SignatureFormat,
            outOfRangeCount = completion.OutOfRangeCount,
            failedCheckCount = completion.FailedCheckCount,
            versionNo = version.VersionNo,
            definition = version.Definition,
            answers = completion.Answers,
        });
    }

    private static async Task<IResult> CompleteAsync(
        int id,
        [FromBody] CompleteRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        // Replay check first. The mobile app queues writes when signal drops
        // and replays them; if the original response was lost in transit, the
        // retry must return the original result rather than a duplicate error.
        if (request.ClientSubmissionId is { } submissionId)
        {
            var existing = await db.PmCompletions.AsNoTracking()
                .SingleOrDefaultAsync(c => c.ClientSubmissionId == submissionId, ct);

            if (existing is not null)
            {
                return Results.Ok(new
                {
                    completionId = existing.Id,
                    outOfRangeCount = existing.OutOfRangeCount,
                    failedCheckCount = existing.FailedCheckCount,
                    replayed = true,
                });
            }
        }

        var task = await db.PmTasks.SingleOrDefaultAsync(t => t.Id == id, ct);
        if (task is null)
        {
            return Results.NotFound();
        }

        if (task.Status is PmTaskStatus.Completed or PmTaskStatus.Skipped)
        {
            return Results.Conflict(new { error = "This PM has already been closed." });
        }

        // The version the device actually rendered, not whatever is published
        // now. A checklist can be superseded while a technician is mid-round
        // on a ward with no signal, and the honest record is the questions
        // they were asked.
        var version = await db.ChecklistTemplateVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == request.ChecklistTemplateVersionId, ct);

        if (version is null)
        {
            return Results.BadRequest(new { error = "Unknown checklist version." });
        }

        var templateId = await db.PmSchedules
            .Where(s => s.Id == task.PmScheduleId)
            .Select(s => s.ChecklistTemplateId)
            .SingleAsync(ct);

        if (version.ChecklistTemplateId != templateId)
        {
            return Results.BadRequest(new
            {
                error = "That checklist version belongs to a different checklist.",
            });
        }

        if (version.Status == ChecklistVersionStatus.Draft)
        {
            // A draft is unpublished and still editable, so a PM recorded
            // against one could have its questions rewritten afterwards.
            return Results.BadRequest(new
            {
                error = "A PM cannot be recorded against an unpublished draft.",
            });
        }

        var answers = request.Answers?.ToDictionary(
            kv => kv.Key,
            kv => new ChecklistAnswer { Value = kv.Value.Value ?? string.Empty, Note = kv.Value.Note },
            StringComparer.Ordinal) ?? [];

        var validation = ChecklistAnswerValidator.Validate(version.Definition, answers);

        if (!validation.IsValid)
        {
            return Results.BadRequest(new
            {
                error = "The checklist is not complete.",
                problems = validation.Problems.Select(p => new { item = p.Path, message = p.Message }),
            });
        }

        byte[]? signature = null;
        var signatureFormat = (request.SignatureFormat ?? "png").Trim().ToLowerInvariant();

        if (signatureFormat is not ("png" or "svg"))
        {
            return Results.BadRequest(new { error = "Signature format must be png or svg." });
        }

        if (!string.IsNullOrWhiteSpace(request.SignatureBase64))
        {
            // Data URL prefix stripped: a canvas toDataURL() includes it and
            // making every client remember to remove it invites a mangled
            // image stored as evidence.
            var raw = request.SignatureBase64;
            var comma = raw.IndexOf(',', StringComparison.Ordinal);
            if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            {
                raw = raw[(comma + 1)..];
            }

            try
            {
                signature = Convert.FromBase64String(raw);
            }
            catch (FormatException)
            {
                return Results.BadRequest(new { error = "The signature image could not be read." });
            }

            if (signature.Length > MaxSignatureBytes)
            {
                return Results.BadRequest(new { error = "The signature image is too large." });
            }
        }

        var userId = int.Parse(
            principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? "0",
            System.Globalization.CultureInfo.InvariantCulture);

        var now = clock.GetUtcNow().UtcDateTime;

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var completion = new PmCompletion
        {
            PmTaskId = task.Id,
            TenantId = task.TenantId,
            ChecklistTemplateVersionId = version.Id,
            Answers = validation.Normalised.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            Signature = signature,
            SignatureFormat = signature is null ? null : signatureFormat,
            SignedByName = request.SignedByName?.Trim(),
            CompletedByUserId = userId,
            CompletedAtUtc = now,
            // Clamped: a device with a wrong clock must not record work as
            // done in the future, which would break every compliance window.
            PerformedAtUtc = request.PerformedAtUtc is { } performed && performed <= now ? performed : now,
            ClientSubmissionId = request.ClientSubmissionId,
            Notes = request.Notes?.Trim(),
        };

        db.PmCompletions.Add(completion);

        task.Status = PmTaskStatus.Completed;
        task.CompletedAtUtc = now;
        task.CompletedByUserId = userId;
        task.ChecklistTemplateVersionId = version.Id;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Results.Ok(new
        {
            completionId = completion.Id,
            outOfRangeCount = completion.OutOfRangeCount,
            failedCheckCount = completion.FailedCheckCount,
            replayed = false,
        });
    }

    private static async Task<IResult> SkipAsync(
        int id,
        [FromBody] SkipRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Results.BadRequest(new { error = "A skip needs a reason." });
        }

        var task = await db.PmTasks.SingleOrDefaultAsync(t => t.Id == id, ct);
        if (task is null)
        {
            return Results.NotFound();
        }

        if (task.Status is PmTaskStatus.Completed or PmTaskStatus.Skipped)
        {
            return Results.Conflict(new { error = "This PM has already been closed." });
        }

        task.Status = PmTaskStatus.Skipped;
        task.SkipReason = request.Reason.Trim();
        task.CompletedAtUtc = clock.GetUtcNow().UtcDateTime;
        task.CompletedByUserId = int.Parse(
            principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub) ?? "0",
            System.Globalization.CultureInfo.InvariantCulture);

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }
}
