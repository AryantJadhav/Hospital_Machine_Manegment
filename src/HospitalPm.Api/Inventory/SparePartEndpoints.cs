using HospitalPm.Api.Auth;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Inventory;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// See EquipmentEndpoints: these expressions are LINQ-to-SQL, where ToLower() is
// what translates and the StringComparison overloads CA1862 asks for do not.
#pragma warning disable CA1862

namespace HospitalPm.Api.Inventory;

public sealed record SparePartRequest(
    string PartNumber,
    string Name,
    string? Description,
    int? EquipmentTypeId,
    string? Unit,
    int QuantityOnHand,
    decimal? UnitCost,
    string? Supplier,
    string? StorageLocation,
    DateOnly? PurchaseDate,
    int? WarrantyMonths,
    string? Notes,
    // Left out on a new part, which is active; on an edit, left out keeps the
    // state it has - the same convention as an equipment type.
    bool? IsActive);

public sealed record SparePartResponse(
    int Id,
    string PartNumber,
    string Name,
    string? Description,
    int? EquipmentTypeId,
    string? EquipmentTypeName,
    string Unit,
    int QuantityOnHand,
    decimal? UnitCost,
    string? Supplier,
    string? StorageLocation,
    DateOnly? PurchaseDate,
    int? WarrantyMonths,
    DateOnly? WarrantyExpiryDate,
    string? Notes,
    bool IsActive,
    string StockStatus,
    bool IsLow);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>
/// The biomedical department's own shelf of spares: fuses, tubing sets, sensor
/// probes, filters - counted, not individually tracked, the way a real store
/// register works.
///
/// Every signed-in user can read it - an engineer checking whether a replacement
/// is on the shelf before promising a repair date is the whole point of keeping
/// one. Only an Administrator adjusts stock and adds new lines, the same split
/// as the equipment register.
/// </summary>
public static class SparePartEndpoints
{
    private const int MaxPageSize = 200;
    private const int MaxPartNumberLength = 64;
    private const int MaxNameLength = 200;

    /// <summary>Fifty years: longer than any warranty on a part has ever run, so a bigger number is a typo.</summary>
    private const int MaxWarrantyMonths = 600;

    public static void MapSparePartEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/spare-parts").WithTags("Spare parts").RequireAuthorization();

        group.MapGet("/", SearchAsync);
        group.MapGet("/{id:int}", GetAsync);

        group.MapPost("/", CreateAsync).RequirePermission(Permissions.SparePartsEdit);
        group.MapPut("/{id:int}", UpdateAsync).RequirePermission(Permissions.SparePartsEdit);
    }

    private static async Task<IResult> SearchAsync(
        HospitalPmDbContext db,
        [FromQuery] string? q,
        // "out": none left. "low": one to five left (see StockRule). Apart on purpose, so the one list is
        // what to order today and the other is what to order soon.
        [FromQuery] string? stock,
        [FromQuery] bool includeInactive = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.SpareParts.AsNoTracking();

        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(p =>
                p.PartNumber.ToLower().Contains(term) ||
                p.Name.ToLower().Contains(term) ||
                (p.Supplier != null && p.Supplier.ToLower().Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(stock))
        {
            switch (stock.Trim().ToLowerInvariant())
            {
                case "out":
                    query = query.Where(p => p.QuantityOnHand <= 0);
                    break;
                case "low":
                    query = query.Where(p => p.QuantityOnHand > 0 && p.QuantityOnHand <= StockRule.LowStockMax);
                    break;
                default:
                    return Results.BadRequest(new { error = "Stock can be 'out' or 'low'." });
            }
        }

        var total = await query.CountAsync(ct);

        // Fetched as entities, not projected straight to the response: ToResponse
        // reads a navigation property and does arithmetic EF cannot turn into SQL.
        var rows = await query
            .Include(p => p.EquipmentType)
            .OrderBy(p => p.PartNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<SparePartResponse>(rows.Select(ToResponse).ToList(), total, page, pageSize));
    }

    private static async Task<IResult> GetAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        var part = await db.SpareParts.AsNoTracking()
            .Include(p => p.EquipmentType)
            .SingleOrDefaultAsync(p => p.Id == id, ct);

        return part is null ? Results.NotFound() : Results.Ok(ToResponse(part));
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] SparePartRequest request, HospitalPmDbContext db, CancellationToken ct)
    {
        var error = await ValidateAsync(request, existingId: null, db, ct);
        if (error is not null)
        {
            return error;
        }

        var part = new SparePart
        {
            PartNumber = request.PartNumber.Trim(),
            Name = request.Name.Trim(),
            Description = Blank(request.Description),
            EquipmentTypeId = request.EquipmentTypeId,
            Unit = string.IsNullOrWhiteSpace(request.Unit) ? "pcs" : request.Unit.Trim(),
            QuantityOnHand = request.QuantityOnHand,
            UnitCost = request.UnitCost,
            Supplier = Blank(request.Supplier),
            StorageLocation = Blank(request.StorageLocation),
            PurchaseDate = request.PurchaseDate,
            WarrantyMonths = request.WarrantyMonths,
            Notes = Blank(request.Notes),
            IsActive = request.IsActive ?? true,
        };

        db.SpareParts.Add(part);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/spare-parts/{part.Id}", new { part.Id, part.PartNumber });
    }

    private static async Task<IResult> UpdateAsync(
        int id, [FromBody] SparePartRequest request, HospitalPmDbContext db, CancellationToken ct)
    {
        var part = await db.SpareParts.SingleOrDefaultAsync(p => p.Id == id, ct);
        if (part is null)
        {
            return Results.NotFound();
        }

        var error = await ValidateAsync(request, existingId: id, db, ct);
        if (error is not null)
        {
            return error;
        }

        // The part number is not touched here on purpose, the same as an equipment
        // type's code: it is what the bin label and any spreadsheet reference say,
        // and a save from this form should not silently move the label.
        part.Name = request.Name.Trim();
        part.Description = Blank(request.Description);
        part.EquipmentTypeId = request.EquipmentTypeId;
        part.Unit = string.IsNullOrWhiteSpace(request.Unit) ? "pcs" : request.Unit.Trim();
        part.QuantityOnHand = request.QuantityOnHand;
        part.UnitCost = request.UnitCost;
        part.Supplier = Blank(request.Supplier);
        part.StorageLocation = Blank(request.StorageLocation);
        part.PurchaseDate = request.PurchaseDate;
        part.WarrantyMonths = request.WarrantyMonths;
        part.Notes = Blank(request.Notes);
        part.IsActive = request.IsActive ?? part.IsActive;

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult?> ValidateAsync(
        SparePartRequest request, int? existingId, HospitalPmDbContext db, CancellationToken ct)
    {
        var partNumber = request.PartNumber?.Trim() ?? string.Empty;
        if (partNumber.Length == 0)
        {
            return Results.BadRequest(new { error = "Give the part a stock number." });
        }

        if (partNumber.Length > MaxPartNumberLength)
        {
            return Results.BadRequest(new { error = $"The part number can be at most {MaxPartNumberLength} characters." });
        }

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return Results.BadRequest(new { error = "Give the part a name." });
        }

        if (name.Length > MaxNameLength)
        {
            return Results.BadRequest(new { error = $"The name can be at most {MaxNameLength} characters." });
        }

        var lowered = partNumber.ToLower();
        if (await db.SpareParts.AnyAsync(p => p.Id != existingId && p.PartNumber.ToLower() == lowered, ct))
        {
            return Results.Conflict(new { error = $"Part number '{partNumber}' is already in use." });
        }

        if (request.QuantityOnHand < 0)
        {
            return Results.BadRequest(new { error = "Quantity on hand cannot be negative." });
        }

        if (request.UnitCost is < 0)
        {
            return Results.BadRequest(new { error = "Unit cost cannot be negative." });
        }

        if (request.EquipmentTypeId is { } typeId && !await db.EquipmentTypes.AnyAsync(t => t.Id == typeId, ct))
        {
            return Results.BadRequest(new { error = "Unknown equipment type." });
        }

        if (request.WarrantyMonths is { } months)
        {
            if (months is < 1 or > MaxWarrantyMonths)
            {
                return Results.BadRequest(new { error = $"The warranty must be between 1 and {MaxWarrantyMonths} months." });
            }

            // The warranty runs from the day it was bought, so it cannot be worked out without it.
            if (request.PurchaseDate is null)
            {
                return Results.BadRequest(new { error = "Give the date of purchase for the warranty to run from." });
            }
        }

        return null;
    }

    private static SparePartResponse ToResponse(SparePart p) => new(
        p.Id,
        p.PartNumber,
        p.Name,
        p.Description,
        p.EquipmentTypeId,
        p.EquipmentType == null ? null : p.EquipmentType.Name,
        p.Unit,
        p.QuantityOnHand,
        p.UnitCost,
        p.Supplier,
        p.StorageLocation,
        p.PurchaseDate,
        p.WarrantyMonths,
        p.WarrantyExpiryDate,
        p.Notes,
        p.IsActive,
        StockRule.For(p.QuantityOnHand).ToString().ToLowerInvariant(),
        StockRule.For(p.QuantityOnHand) != StockLevel.Ok);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
