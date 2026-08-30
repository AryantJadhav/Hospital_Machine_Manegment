using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// CA1862 recommends the StringComparison overloads for case-insensitive
// comparison. That advice is correct for in-memory strings and wrong here:
// these expressions are LINQ-to-SQL and EF Core cannot translate a
// StringComparison overload, so it throws at runtime instead of querying.
// ToLower() translates to SQL lower(), which is what the supporting index
// ix_equipment_asset_tag_lower is built on.
#pragma warning disable CA1862

namespace HospitalPm.Api.Equipment;

public sealed record EquipmentRequest(
    string AssetTag,
    string? SerialNumber,
    int EquipmentTypeId,
    int LocationId,
    string? Manufacturer,
    string? Model,
    EquipmentStatus Status,
    DateOnly? PurchaseDate,
    DateOnly? InstallationDate,
    DateOnly? WarrantyExpiryDate,
    string? Notes);

public sealed record EquipmentResponse(
    int Id,
    string AssetTag,
    string? SerialNumber,
    int EquipmentTypeId,
    string EquipmentTypeName,
    int LocationId,
    string LocationName,
    string? Manufacturer,
    string? Model,
    EquipmentStatus Status,
    DateOnly? PurchaseDate,
    DateOnly? InstallationDate,
    DateOnly? WarrantyExpiryDate,
    string? Notes);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public static class EquipmentEndpoints
{
    /// <summary>
    /// Capped so a caller cannot ask for all 15,000 assets in one response
    /// and time the request out.
    /// </summary>
    private const int MaxPageSize = 200;

    public static void MapEquipmentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/equipment")
            .WithTags("Equipment")
            .RequireAuthorization();

        // Every role can read the register: a technician needs to look up the
        // machine in front of them.
        group.MapGet("/", SearchAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapGet("/by-tag/{assetTag}", GetByTagAsync);

        // Writes are restricted. A technician records work against equipment;
        // they do not add or retire assets on the register.
        group.MapPost("/", CreateAsync)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin, Roles.BiomedicalHead, Roles.SeniorEngineer));

        group.MapPut("/{id:int}", UpdateAsync)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin, Roles.BiomedicalHead, Roles.SeniorEngineer));

        // Retiring an asset is a register-owner decision, not an engineer's.
        group.MapPost("/{id:int}/condemn", CondemnAsync)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin, Roles.BiomedicalHead));
    }

    private static async Task<IResult> SearchAsync(
        HospitalPmDbContext db,
        [FromQuery] string? q,
        [FromQuery] int? locationId,
        [FromQuery] int? equipmentTypeId,
        [FromQuery] EquipmentStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.Equipment.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLowerInvariant();
            query = query.Where(e =>
                e.AssetTag.ToLower().Contains(term) ||
                (e.SerialNumber != null && e.SerialNumber.ToLower().Contains(term)) ||
                (e.Manufacturer != null && e.Manufacturer.ToLower().Contains(term)) ||
                (e.Model != null && e.Model.ToLower().Contains(term)));
        }

        if (locationId is not null)
        {
            // Subtree search: asking for a site must return everything in
            // every department under it, not just assets pinned to the site
            // row itself. The materialised path makes this a prefix scan
            // rather than a recursive walk.
            var prefix = await db.Locations
                .Where(l => l.Id == locationId)
                .Select(l => l.Path)
                .SingleOrDefaultAsync(ct);

            if (prefix is null)
            {
                return Results.NotFound(new { error = "Unknown location." });
            }

            query = query.Where(e => e.Location!.Path.StartsWith(prefix));
        }

        if (equipmentTypeId is not null)
        {
            query = query.Where(e => e.EquipmentTypeId == equipmentTypeId);
        }

        if (status is not null)
        {
            query = query.Where(e => e.Status == status);
        }

        var total = await query.CountAsync(ct);

        var items = await query
            .OrderBy(e => e.AssetTag)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new EquipmentResponse(
                e.Id,
                e.AssetTag,
                e.SerialNumber,
                e.EquipmentTypeId,
                e.EquipmentType!.Name,
                e.LocationId,
                e.Location!.Name,
                e.Manufacturer,
                e.Model,
                e.Status,
                e.PurchaseDate,
                e.InstallationDate,
                e.WarrantyExpiryDate,
                e.Notes))
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<EquipmentResponse>(items, total, page, pageSize));
    }

    private static async Task<IResult> GetAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        var item = await db.Equipment.AsNoTracking()
            .Where(e => e.Id == id)
            .Select(e => new EquipmentResponse(
                e.Id,
                e.AssetTag,
                e.SerialNumber,
                e.EquipmentTypeId,
                e.EquipmentType!.Name,
                e.LocationId,
                e.Location!.Name,
                e.Manufacturer,
                e.Model,
                e.Status,
                e.PurchaseDate,
                e.InstallationDate,
                e.WarrantyExpiryDate,
                e.Notes))
            .SingleOrDefaultAsync(ct);

        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    /// <summary>
    /// Resolves a scanned QR tag. Case-insensitive: a technician who types
    /// the tag by hand when a label is too scratched to scan should still
    /// find the machine.
    /// </summary>
    private static async Task<IResult> GetByTagAsync(string assetTag, HospitalPmDbContext db, CancellationToken ct)
    {
        var normalised = assetTag.Trim().ToLowerInvariant();

        var item = await db.Equipment.AsNoTracking()
            .Where(e => e.AssetTag.ToLower() == normalised)
            .Select(e => new EquipmentResponse(
                e.Id,
                e.AssetTag,
                e.SerialNumber,
                e.EquipmentTypeId,
                e.EquipmentType!.Name,
                e.LocationId,
                e.Location!.Name,
                e.Manufacturer,
                e.Model,
                e.Status,
                e.PurchaseDate,
                e.InstallationDate,
                e.WarrantyExpiryDate,
                e.Notes))
            .SingleOrDefaultAsync(ct);

        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] EquipmentRequest request,
        HospitalPmDbContext db,
        CancellationToken ct)
    {
        var error = await ValidateAsync(request, db, ct);
        if (error is not null)
        {
            return error;
        }

        if (await db.Equipment.AnyAsync(e => e.AssetTag.ToLower() == request.AssetTag.Trim().ToLower(), ct))
        {
            return Results.Conflict(new { error = $"Asset tag '{request.AssetTag}' is already in use." });
        }

        var entity = new Domain.Assets.Equipment
        {
            AssetTag = request.AssetTag.Trim(),
            SerialNumber = request.SerialNumber?.Trim(),
            EquipmentTypeId = request.EquipmentTypeId,
            LocationId = request.LocationId,
            Manufacturer = request.Manufacturer?.Trim(),
            Model = request.Model?.Trim(),
            Status = request.Status,
            PurchaseDate = request.PurchaseDate,
            InstallationDate = request.InstallationDate,
            WarrantyExpiryDate = request.WarrantyExpiryDate,
            Notes = request.Notes,
        };

        db.Equipment.Add(entity);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/equipment/{entity.Id}", new { entity.Id });
    }

    private static async Task<IResult> UpdateAsync(
        int id,
        [FromBody] EquipmentRequest request,
        HospitalPmDbContext db,
        CancellationToken ct)
    {
        var entity = await db.Equipment.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null)
        {
            return Results.NotFound();
        }

        var error = await ValidateAsync(request, db, ct);
        if (error is not null)
        {
            return error;
        }

        var tag = request.AssetTag.Trim();
        if (await db.Equipment.AnyAsync(e => e.Id != id && e.AssetTag.ToLower() == tag.ToLower(), ct))
        {
            return Results.Conflict(new { error = $"Asset tag '{tag}' is already in use." });
        }

        entity.AssetTag = tag;
        entity.SerialNumber = request.SerialNumber?.Trim();
        entity.EquipmentTypeId = request.EquipmentTypeId;
        entity.LocationId = request.LocationId;
        entity.Manufacturer = request.Manufacturer?.Trim();
        entity.Model = request.Model?.Trim();
        entity.Status = request.Status;
        entity.PurchaseDate = request.PurchaseDate;
        entity.InstallationDate = request.InstallationDate;
        entity.WarrantyExpiryDate = request.WarrantyExpiryDate;
        entity.Notes = request.Notes;

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Withdraws an asset from service. Deliberately not a DELETE: completed
    /// PM certificates and work orders reference this row and must stay
    /// readable for years, so the register keeps condemned assets.
    /// </summary>
    private static async Task<IResult> CondemnAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        var entity = await db.Equipment.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (entity is null)
        {
            return Results.NotFound();
        }

        entity.Status = EquipmentStatus.Condemned;
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult?> ValidateAsync(
        EquipmentRequest request, HospitalPmDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.AssetTag))
        {
            return Results.BadRequest(new { error = "Asset tag is required." });
        }

        if (!await db.EquipmentTypes.AnyAsync(t => t.Id == request.EquipmentTypeId, ct))
        {
            return Results.BadRequest(new { error = "Unknown equipment type." });
        }

        var level = await db.Locations
            .Where(l => l.Id == request.LocationId)
            .Select(l => (int?)l.Level)
            .SingleOrDefaultAsync(ct);

        if (level is null)
        {
            return Results.BadRequest(new { error = "Unknown location." });
        }

        // Mirrors the database trigger so the caller gets a 400 with a clear
        // message rather than a 500 from a raised Postgres exception.
        if (level < (int)Domain.Locations.LocationLevel.Building)
        {
            return Results.BadRequest(new
            {
                error = "Equipment must be placed at building level or deeper, not at an organisation or site.",
            });
        }

        return null;
    }
}
