using System.Globalization;
using System.Security.Claims;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Maintenance;

public sealed record ReportFileResponse(
    int Id,
    string FileName,
    string ContentType,
    int SizeBytes,
    string? UploadedBy,
    DateTime UploadedAtUtc);

/// <summary>
/// PMs that the maintenance contract vendor does.
///
/// Where a machine is under an AMC or a CMC, its PM can be set to the vendor's. The vendor
/// does the work and hands over a service report. Our staff record that it was done, on the
/// vendor's behalf, and save the report, a PDF or a photo, against the PM.
///
/// There is no checklist and no drawn signature for these: the vendor's report is the
/// record. It is stored in the database, so the nightly backup, a restore and the export all
/// carry it, and it can be added to later.
/// </summary>
public static class PmVendorEndpoints
{
    /// <summary>The whole request, all files together. Ten files of ten megabytes is a mistake.</summary>
    private const long MaxRequestBytes = 40L * 1024 * 1024;

    public static void MapPmVendorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pm").WithTags("PM vendor reports").RequireAuthorization();

        // Everyone signed in: the report is part of the machine's record, and recording that a
        // vendor finished is a matter of writing down what happened, as it is for an in-house PM.
        group.MapGet("/tasks/{id:int}/vendor", VendorAsync);
        group.MapPost("/tasks/{id:int}/complete-by-vendor", CompleteAsync).DisableAntiforgery();
        group.MapPost("/tasks/{id:int}/attachments", AddFilesAsync).DisableAntiforgery();
        group.MapGet("/attachments/{attachmentId:int}", DownloadAsync);

        // Removing a file is an Administrator's. A report is evidence, so it is not for whoever
        // is holding the list; but a photo that should never have been uploaded, one showing
        // a patient by mistake, has to be removable.
        group.MapDelete("/attachments/{attachmentId:int}", DeleteAsync)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }

    private static int UserId(ClaimsPrincipal principal) => int.Parse(
        principal.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
        ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "0",
        CultureInfo.InvariantCulture);

    /// <summary>
    /// What the page for a vendor's PM needs: which machine and contract, whether it has been
    /// done and by whom, and the files saved against it. For a PM that is not the vendor's it
    /// says so, and the caller shows the ordinary checklist.
    /// </summary>
    private static async Task<IResult> VendorAsync(
        int id, HospitalPmDbContext db, HospitalClock clock, CancellationToken ct)
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
                ChecklistName = t.Schedule!.ChecklistTemplate!.Name,
                t.Schedule!.ChecklistTemplateId,
                t.Schedule!.PerformedBy,
                ContractType = t.Equipment!.MaintenanceContractType,
                t.Equipment!.MaintenanceVendor,
                t.Equipment!.MaintenanceContractNumber,
            })
            .SingleOrDefaultAsync(ct);

        if (task is null)
        {
            return Results.NotFound();
        }

        if (task.PerformedBy != PmPerformedBy.Vendor)
        {
            return Results.Ok(new { isVendor = false });
        }

        var completion = await db.PmCompletions.AsNoTracking()
            .Where(c => c.PmTaskId == id)
            .Select(c => new
            {
                c.PerformedAtUtc,
                c.CompletedAtUtc,
                c.SignedByName,
                c.Notes,
                c.VendorName,
                RecordedBy = db.Users.Where(u => u.Id == c.CompletedByUserId).Select(u => u.FullName).FirstOrDefault(),
            })
            .SingleOrDefaultAsync(ct);

        var files = await FilesAsync(db, id, ct);

        var canComplete = await db.ChecklistTemplateVersions.AnyAsync(
            v => v.ChecklistTemplateId == task.ChecklistTemplateId && v.Status == ChecklistVersionStatus.Published, ct);

        return Results.Ok(new
        {
            isVendor = true,
            taskId = task.Id,
            task.Status,
            task.DueDate,
            task.EquipmentId,
            task.AssetTag,
            task.EquipmentTypeName,
            task.LocationName,
            task.ChecklistName,
            vendorName = completion?.VendorName ?? task.MaintenanceVendor,
            contractType = task.ContractType,
            contractNumber = task.MaintenanceContractNumber,
            canComplete,
            today = clock.Today(),
            completion = completion is null
                ? null
                : new
                {
                    performedOn = completion.PerformedAtUtc is { } p
                        ? (DateOnly?)DateOnly.FromDateTime(p.Add(clock.Offset))
                        : null,
                    engineerName = completion.SignedByName,
                    completion.Notes,
                    completion.RecordedBy,
                    completion.CompletedAtUtc,
                },
            files,
        });
    }

    private static async Task<List<ReportFileResponse>> FilesAsync(HospitalPmDbContext db, int taskId, CancellationToken ct)
        => await db.PmTaskAttachments.AsNoTracking()
            .Where(a => a.PmTaskId == taskId)
            .OrderBy(a => a.UploadedAtUtc).ThenBy(a => a.Id)
            .Select(a => new ReportFileResponse(
                a.Id,
                a.FileName,
                a.ContentType,
                a.SizeBytes,
                db.Users.Where(u => u.Id == a.UploadedByUserId).Select(u => u.FullName).FirstOrDefault(),
                a.UploadedAtUtc))
            .ToListAsync(ct);

    /// <summary>
    /// The form that was sent, or null when it cannot be read: an empty upload, a cut-off one,
    /// or something that is not a form at all. That is the caller's mistake, so it is a 400
    /// and not a server error.
    /// </summary>
    private static async Task<IFormCollection?> TryReadFormAsync(HttpRequest request, CancellationToken ct)
    {
        try
        {
            return await request.ReadFormAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        {
            return null;
        }
    }

    /// <summary>The files in a form, checked. Returns an error result, or the accepted files.</summary>
    private static async Task<(IResult? Error, List<(string Name, string Type, byte[] Bytes)> Files)> ReadFilesAsync(
        IFormFileCollection files, int alreadyStored, CancellationToken ct)
    {
        var accepted = new List<(string Name, string Type, byte[] Bytes)>();

        if (alreadyStored + files.Count > ReportFile.MaxFilesPerPm)
        {
            return (Results.BadRequest(new
            {
                error = $"A PM can have at most {ReportFile.MaxFilesPerPm} report files.",
            }), accepted);
        }

        foreach (var file in files)
        {
            if (file.Length == 0)
            {
                return (Results.BadRequest(new { error = $"'{Path.GetFileName(file.FileName)}' is empty." }), accepted);
            }

            if (file.Length > ReportFile.MaxBytes)
            {
                return (Results.BadRequest(new
                {
                    error = $"'{Path.GetFileName(file.FileName)}' is larger than {ReportFile.MaxBytes / (1024 * 1024)} MB.",
                }), accepted);
            }

            using var buffer = new MemoryStream((int)file.Length);
            await file.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();

            var type = ReportFile.Sniff(bytes);
            if (type is null)
            {
                return (Results.BadRequest(new
                {
                    error = $"'{Path.GetFileName(file.FileName)}' is not a PDF or a photo (JPEG, PNG or WebP).",
                }), accepted);
            }

            accepted.Add((ReportFile.CleanName(file.FileName, type), type, bytes));
        }

        return (null, accepted);
    }

    private static void Store(
        HospitalPmDbContext db, int taskId, int tenantId, int userId, DateTime now,
        IEnumerable<(string Name, string Type, byte[] Bytes)> files)
    {
        foreach (var (name, type, bytes) in files)
        {
            db.PmTaskAttachments.Add(new PmTaskAttachment
            {
                PmTaskId = taskId,
                TenantId = tenantId,
                FileName = name,
                ContentType = type,
                SizeBytes = bytes.Length,
                UploadedByUserId = userId,
                UploadedAtUtc = now,
                Data = new PmTaskAttachmentData { Data = bytes },
            });
        }
    }

    /// <summary>
    /// Records that the vendor did this PM: the day they did it, their engineer's name, any
    /// notes, and their report as one or more files. Files may also be added afterwards, as
    /// a report often arrives after the visit.
    /// </summary>
    private static async Task<IResult> CompleteAsync(
        int id,
        HttpRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (!request.HasFormContentType || request.ContentLength > MaxRequestBytes)
        {
            return Results.BadRequest(new { error = "Send the vendor's details and the report files as a form." });
        }

        var form = await TryReadFormAsync(request, ct);
        if (form is null)
        {
            return Results.BadRequest(new { error = "The upload could not be read. Send the vendor's details and the report files again." });
        }

        if (!DateOnly.TryParseExact(form["performedOn"], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var performedOn))
        {
            return Results.BadRequest(new { error = "Say which day the vendor did the PM." });
        }

        var today = clock.Today();
        if (performedOn > today)
        {
            // Not in the future: a PM cannot have been done tomorrow, and every compliance
            // window is counted from the day of the work.
            return Results.BadRequest(new { error = "The vendor cannot have done the PM on a day that has not come yet." });
        }

        var engineer = form["engineerName"].ToString().Trim();
        var notes = form["notes"].ToString().Trim();
        if (engineer.Length > 200 || notes.Length > 2000)
        {
            return Results.BadRequest(new { error = "The engineer's name or the notes are too long." });
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

        var schedule = await db.PmSchedules.AsNoTracking()
            .Where(s => s.Id == task.PmScheduleId)
            .Select(s => new
            {
                s.PerformedBy,
                s.ChecklistTemplateId,
                Vendor = s.Equipment!.MaintenanceVendor,
                s.Equipment!.MaintenanceContractType,
            })
            .SingleAsync(ct);

        if (schedule.PerformedBy != PmPerformedBy.Vendor)
        {
            return Results.BadRequest(new { error = "This PM is done by the hospital's own team. Fill in its checklist." });
        }

        if (schedule.MaintenanceContractType is null || string.IsNullOrWhiteSpace(schedule.Vendor))
        {
            return Results.Conflict(new
            {
                error = "This machine no longer has a maintenance contract, so there is no vendor to record. " +
                        "Add its contract on the machine's record first.",
            });
        }

        // A completion is pinned to a published checklist version, as every completion is.
        // The vendor is not asked its questions; the version is what the schedule is on.
        var version = await db.ChecklistTemplateVersions.AsNoTracking()
            .SingleOrDefaultAsync(
                v => v.ChecklistTemplateId == schedule.ChecklistTemplateId
                     && v.Status == ChecklistVersionStatus.Published, ct);

        if (version is null)
        {
            return Results.Conflict(new { error = "This PM's checklist has no published version, so it cannot be recorded yet." });
        }

        var (error, files) = await ReadFilesAsync(form.Files, alreadyStored: 0, ct);
        if (error is not null)
        {
            return error;
        }

        var userId = UserId(principal);
        var now = clock.UtcNow();

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var completion = new PmCompletion
        {
            PmTaskId = task.Id,
            TenantId = task.TenantId,
            ChecklistTemplateVersionId = version.Id,
            Answers = [],
            SignedByName = engineer.Length == 0 ? null : engineer,
            CompletedByUserId = userId,
            CompletedAtUtc = now,
            // The day, taken as midday at the hospital, so the date reads the same in any time zone.
            PerformedAtUtc = DateTime.SpecifyKind(performedOn.ToDateTime(new TimeOnly(12, 0)) - clock.Offset, DateTimeKind.Utc),
            Notes = notes.Length == 0 ? null : notes,
            PerformedBy = PmPerformedBy.Vendor,
            VendorName = schedule.Vendor.Trim(),
        };

        db.PmCompletions.Add(completion);

        task.Status = PmTaskStatus.Completed;
        task.CompletedAtUtc = now;
        task.CompletedByUserId = userId;
        task.ChecklistTemplateVersionId = version.Id;

        Store(db, task.Id, task.TenantId, userId, now, files);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Results.Ok(new { completionId = completion.Id, filesSaved = files.Count });
    }

    /// <summary>Adds report files to a PM the vendor has done.</summary>
    private static async Task<IResult> AddFilesAsync(
        int id,
        HttpRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (!request.HasFormContentType || request.ContentLength > MaxRequestBytes)
        {
            return Results.BadRequest(new { error = "Send the report files as a form." });
        }

        var task = await db.PmTasks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct);
        if (task is null)
        {
            return Results.NotFound();
        }

        var byVendor = await db.PmCompletions.AnyAsync(
            c => c.PmTaskId == id && c.PerformedBy == PmPerformedBy.Vendor, ct);

        if (!byVendor)
        {
            return Results.Conflict(new { error = "Report files are added to a PM that the vendor has done." });
        }

        var form = await TryReadFormAsync(request, ct);
        if (form is null || form.Files.Count == 0)
        {
            return Results.BadRequest(new { error = "Choose a PDF or a photo to save." });
        }

        var stored = await db.PmTaskAttachments.CountAsync(a => a.PmTaskId == id, ct);
        var (error, files) = await ReadFilesAsync(form.Files, stored, ct);
        if (error is not null)
        {
            return error;
        }

        Store(db, id, task.TenantId, UserId(principal), clock.UtcNow(), files);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { filesSaved = files.Count });
    }

    private static async Task<IResult> DownloadAsync(int attachmentId, HttpContext http, HospitalPmDbContext db, CancellationToken ct)
    {
        var file = await db.PmTaskAttachments.AsNoTracking()
            .Where(a => a.Id == attachmentId)
            .Select(a => new { a.FileName, a.ContentType, a.Data!.Data })
            .SingleOrDefaultAsync(ct);

        if (file is null)
        {
            return Results.NotFound();
        }

        // Shown in the browser, but only ever as the type the bytes were found to be, and
        // told not to be sniffed into anything else.
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Content-Disposition"] =
            $"inline; filename=\"report\"; filename*=UTF-8''{Uri.EscapeDataString(file.FileName)}";

        return Results.Bytes(file.Data, file.ContentType);
    }

    private static async Task<IResult> DeleteAsync(int attachmentId, HospitalPmDbContext db, CancellationToken ct)
    {
        var attachment = await db.PmTaskAttachments.SingleOrDefaultAsync(a => a.Id == attachmentId, ct);
        if (attachment is null)
        {
            return Results.NotFound();
        }

        // The bytes go with it; the audit log keeps a note that it was there and who removed it.
        db.PmTaskAttachments.Remove(attachment);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }
}
