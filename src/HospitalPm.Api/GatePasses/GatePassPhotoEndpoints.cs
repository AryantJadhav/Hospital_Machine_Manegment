using System.Security.Claims;
using HospitalPm.Api.Auth;
using HospitalPm.Api.Equipment;
using HospitalPm.Api.Maintenance;
using HospitalPm.Domain.GatePasses;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.GatePasses;

/// <summary>One photo on a gate pass, as the pass's page lists it.</summary>
public sealed record GatePassPhotoResponse(
    int Id, string FileName, string ContentType, int SizeBytes, string? UploadedBy, DateTime UploadedAtUtc);

/// <summary>
/// Photos on a gate pass: what was sent out, in what condition, or what came back. Always optional: a pass is complete without
/// one. They can be added at any time and to a pass of any state, because the photo of a machine as it came back is taken after
/// the pass was written. Removing one is an Administrator's act, as on a service request.
/// </summary>
public static class GatePassPhotoEndpoints
{
    /// <summary>Ten photos of ten megabytes each in one request is a mistake, not a real upload.</summary>
    private const long MaxRequestBytes = 40L * 1024 * 1024;

    /// <summary>A generous ceiling, not a target.</summary>
    public const int MaxPhotosPerPass = 10;

    public static void MapPhotoEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/{id:int}/photos", AddAsync).DisableAntiforgery().RequirePermission(Permissions.GatePassEdit);
        group.MapGet("/photos/{attachmentId:int}", DownloadAsync);
        group.MapDelete("/photos/{attachmentId:int}", DeleteAsync).RequirePermission(Permissions.AttachmentsDelete);
    }

    public static async Task<List<GatePassPhotoResponse>> ListAsync(HospitalPmDbContext db, int passId, CancellationToken ct)
        => await db.GatePassAttachments.AsNoTracking()
            .Where(a => a.GatePassId == passId)
            .OrderBy(a => a.UploadedAtUtc).ThenBy(a => a.Id)
            .Select(a => new GatePassPhotoResponse(
                a.Id,
                a.FileName,
                a.ContentType,
                a.SizeBytes,
                db.Users.Where(u => u.Id == a.UploadedByUserId).Select(u => u.FullName).FirstOrDefault(),
                a.UploadedAtUtc))
            .ToListAsync(ct);

    private static async Task<IResult> AddAsync(
        int id,
        HttpRequest request,
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        TimeProvider clock,
        CancellationToken ct)
    {
        if (!request.HasFormContentType || request.ContentLength > MaxRequestBytes)
        {
            return Results.BadRequest(new { error = "Send the photos as a form." });
        }

        if (!await db.GatePasses.AnyAsync(p => p.Id == id, ct))
        {
            return Results.NotFound();
        }

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(ct);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or BadHttpRequestException)
        {
            return Results.BadRequest(new { error = "The upload could not be read. Choose the photos again." });
        }

        if (form.Files.Count == 0)
        {
            return Results.BadRequest(new { error = "Choose a photo to save." });
        }

        var stored = await db.GatePassAttachments.CountAsync(a => a.GatePassId == id, ct);
        if (stored + form.Files.Count > MaxPhotosPerPass)
        {
            return Results.BadRequest(new { error = $"A gate pass can have at most {MaxPhotosPerPass} photos." });
        }

        var accepted = new List<(string Name, string Type, byte[] Bytes)>();
        foreach (var file in form.Files)
        {
            var shown = Path.GetFileName(file.FileName);
            if (file.Length == 0)
            {
                return Results.BadRequest(new { error = $"'{shown}' is empty." });
            }

            if (file.Length > ReportFile.MaxBytes)
            {
                return Results.BadRequest(new { error = $"'{shown}' is larger than {ReportFile.MaxBytes / (1024 * 1024)} MB." });
            }

            using var buffer = new MemoryStream((int)file.Length);
            await file.CopyToAsync(buffer, ct);
            var bytes = buffer.ToArray();

            // A photo only, recognised by its first bytes and not by its name.
            var type = ReportFile.Sniff(bytes);
            if (type is null || type == ReportFile.Pdf)
            {
                return Results.BadRequest(new { error = $"'{shown}' is not a photo (JPEG, PNG or WebP)." });
            }

            accepted.Add((ReportFile.CleanName(file.FileName, type), type, bytes));
        }

        var userId = EquipmentMoveEndpoints.UserId(principal);
        var now = clock.GetUtcNow().UtcDateTime;

        foreach (var (name, type, bytes) in accepted)
        {
            db.GatePassAttachments.Add(new GatePassAttachment
            {
                GatePassId = id,
                FileName = name,
                ContentType = type,
                SizeBytes = bytes.Length,
                UploadedByUserId = userId,
                UploadedAtUtc = now,
                Data = new GatePassAttachmentData { Data = bytes },
            });
        }

        await db.SaveChangesAsync(ct);

        return Results.Ok(new { photosSaved = accepted.Count });
    }

    private static async Task<IResult> DownloadAsync(int attachmentId, HttpContext http, HospitalPmDbContext db, CancellationToken ct)
    {
        var file = await db.GatePassAttachments.AsNoTracking()
            .Where(a => a.Id == attachmentId)
            .Select(a => new { a.FileName, a.ContentType, a.Data!.Data })
            .SingleOrDefaultAsync(ct);

        if (file is null)
        {
            return Results.NotFound();
        }

        // Shown in the browser, but only ever as the type the bytes were found to be.
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Content-Disposition"] =
            $"inline; filename=\"photo\"; filename*=UTF-8''{Uri.EscapeDataString(file.FileName)}";

        return Results.Bytes(file.Data, file.ContentType);
    }

    private static async Task<IResult> DeleteAsync(int attachmentId, HospitalPmDbContext db, CancellationToken ct)
    {
        var attachment = await db.GatePassAttachments.SingleOrDefaultAsync(a => a.Id == attachmentId, ct);
        if (attachment is null)
        {
            return Results.NotFound();
        }

        // The bytes go with it; the audit log keeps a note that it was there and who removed it.
        db.GatePassAttachments.Remove(attachment);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }
}
