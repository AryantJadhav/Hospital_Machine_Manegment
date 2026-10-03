using HospitalPm.Api.Auth;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Labels;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// See EquipmentEndpoints: EF Core cannot translate the StringComparison
// overloads CA1862 recommends, and ToLower() maps to SQL lower(), which the
// ix_equipment_asset_tag_lower index is built on.
#pragma warning disable CA1862

namespace HospitalPm.Api.Labels;

public sealed record LabelRequest(int[] EquipmentIds);

public static class LabelEndpoints
{
    /// <summary>
    /// A full sheet is 24 labels; 500 is twenty sheets in one go, which is a
    /// realistic bulk tagging run. Beyond that a single request would hold a
    /// large PDF in memory on a hospital PC.
    /// </summary>
    private const int MaxLabels = 500;

    public static void MapLabelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/labels")
            .WithTags("Labels")
            .RequirePermission(Permissions.RegisterView);

        // A technician standing at a machine with an unreadable label needs to
        // reprint it, so reading a QR is open to every role.
        group.MapGet("/qr/{assetTag}", QrAsync);

        // Bulk printing is a tagging exercise run by the register owner.
        group.MapPost("/sheet", SheetAsync)
            .RequirePermission(Permissions.LabelsPrint);

        group.MapPost("/zpl", ZplAsync)
            .RequirePermission(Permissions.LabelsPrint);
    }

    private static async Task<IResult> QrAsync(
        string assetTag,
        HospitalPmDbContext db,
        QrCodeService qr,
        Microsoft.Extensions.Options.IOptions<LabelOptions> options,
        CancellationToken ct)
    {
        var normalised = assetTag.Trim();

        var exists = await db.Equipment
            .AnyAsync(e => e.AssetTag.ToLower() == normalised.ToLower(), ct);

        if (!exists)
        {
            return Results.NotFound();
        }

        var png = qr.RenderPng(options.Value.BaseUrl, normalised);

        return Results.File(png, "image/png", $"{normalised}.png");
    }

    private static async Task<IResult> SheetAsync(
        [FromBody] LabelRequest request,
        HospitalPmDbContext db,
        LabelSheetService sheets,
        CancellationToken ct)
    {
        var labels = await LoadAsync(request, db, ct);

        return labels.Count == 0
            ? Results.BadRequest(new { error = "No matching equipment." })
            : Results.File(sheets.Render(labels), "application/pdf", "asset-tags.pdf");
    }

    private static async Task<IResult> ZplAsync(
        [FromBody] LabelRequest request,
        HospitalPmDbContext db,
        ZplLabelService zpl,
        CancellationToken ct)
    {
        var labels = await LoadAsync(request, db, ct);

        return labels.Count == 0
            ? Results.BadRequest(new { error = "No matching equipment." })
            // text/plain so a browser shows it and a curl pipe to the
            // printer's raw port works without decoding.
            : Results.Text(zpl.Render(labels), "text/plain", System.Text.Encoding.UTF8);
    }

    private static async Task<List<LabelData>> LoadAsync(
        LabelRequest request, HospitalPmDbContext db, CancellationToken ct)
    {
        if (request?.EquipmentIds is null || request.EquipmentIds.Length == 0)
        {
            return [];
        }

        var ids = request.EquipmentIds.Distinct().Take(MaxLabels).ToArray();

        return await db.Equipment
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            // Ordered by tag so a reprint of the same selection produces the
            // same sheet, and labels come off the printer in a predictable
            // order for someone walking a ward sticking them on.
            .OrderBy(e => e.AssetTag)
            .Select(e => new LabelData(
                e.AssetTag,
                e.EquipmentType!.Name,
                e.Location!.Name,
                e.SerialNumber))
            .ToListAsync(ct);
    }
}
