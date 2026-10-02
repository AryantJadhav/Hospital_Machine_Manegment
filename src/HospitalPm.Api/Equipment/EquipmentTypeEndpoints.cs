using HospitalPm.Api.Auth;
using System.Text;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Equipment;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// See EquipmentEndpoints: these expressions are LINQ-to-SQL, where ToLower() is
// what translates and the StringComparison overloads that CA1862 asks for do not.
#pragma warning disable CA1862

namespace HospitalPm.Api.Equipment;

public sealed record EquipmentTypeRequest(
    string Name,
    string? Description,
    IReadOnlyList<int> CategoryIds,
    int PrimaryCategoryId,
    // Left out on a new type, which is active; on an edit, left out keeps the
    // state it has.
    bool? IsActive);

public sealed record EquipmentTypeCategoryResponse(int CategoryId, string Name, bool IsPrimary);

public sealed record EquipmentTypeResponse(
    int Id,
    string Code,
    string Name,
    string? Description,
    bool IsSeeded,
    bool IsActive,
    int MachineCount,
    IReadOnlyList<EquipmentTypeCategoryResponse> Categories);

/// <summary>
/// Adding and editing the kinds of machine a hospital keeps.
///
/// The starter set ships with the product and a hospital will have kinds of its own
/// that are not in it. An Administrator decides what the department calls a kind of
/// machine and which categories it belongs to; an Employee only picks from the list.
///
/// A type is never deleted. Machines, PM schedules and checklist templates hang off
/// it and must stay readable for years, so a type nobody uses any more is switched
/// off instead, and drops out of the pickers.
/// </summary>
public static class EquipmentTypeEndpoints
{
    private const int MaxNameLength = 200;
    private const int MaxCodeLength = 64;

    public static void MapEquipmentTypeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/equipment-types")
            .WithTags("Equipment types")
            // The whole group, reads included: the pickers everyone uses are on
            // /api/lookups. This is the page for changing them.
            .RequirePermission(Permissions.EquipmentTypesEdit);

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync);
        group.MapPut("/{id:int}", UpdateAsync);
    }

    private static async Task<IResult> ListAsync(HospitalPmDbContext db, CancellationToken ct)
    {
        var counts = await db.Equipment.AsNoTracking()
            .GroupBy(e => e.EquipmentTypeId)
            .Select(g => new { TypeId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TypeId, x => x.Count, ct);

        var types = await db.EquipmentTypes.AsNoTracking()
            .OrderBy(t => t.Name)
            .Select(t => new
            {
                t.Id, t.Code, t.Name, t.Description, t.IsSeeded, t.IsActive,
                Categories = t.Categories
                    .OrderByDescending(c => c.IsPrimary)
                    .ThenBy(c => c.Category!.DisplayOrder)
                    .Select(c => new EquipmentTypeCategoryResponse(c.CategoryId, c.Category!.Name, c.IsPrimary))
                    .ToList(),
            })
            .ToListAsync(ct);

        return Results.Ok(types.Select(t => new EquipmentTypeResponse(
            t.Id, t.Code, t.Name, t.Description, t.IsSeeded, t.IsActive,
            counts.GetValueOrDefault(t.Id), t.Categories)));
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] EquipmentTypeRequest request, HospitalPmDbContext db, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        var error = await ValidateAsync(request, name, existingId: null, db, ct);
        if (error is not null)
        {
            return error;
        }

        var type = new EquipmentType
        {
            Code = await NewCodeAsync(name, db, ct),
            Name = name,
            Description = Blank(request.Description),
            IsSeeded = false,
            IsActive = request.IsActive ?? true,
        };

        foreach (var categoryId in request.CategoryIds.Distinct())
        {
            type.Categories.Add(new EquipmentTypeCategory
            {
                CategoryId = categoryId,
                IsPrimary = categoryId == request.PrimaryCategoryId,
            });
        }

        db.EquipmentTypes.Add(type);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/equipment-types/{type.Id}", new { type.Id, type.Code });
    }

    private static async Task<IResult> UpdateAsync(
        int id, [FromBody] EquipmentTypeRequest request, HospitalPmDbContext db, CancellationToken ct)
    {
        var type = await db.EquipmentTypes.Include(t => t.Categories).SingleOrDefaultAsync(t => t.Id == id, ct);
        if (type is null)
        {
            return Results.NotFound();
        }

        var name = request.Name?.Trim() ?? string.Empty;
        var error = await ValidateAsync(request, name, existingId: id, db, ct);
        if (error is not null)
        {
            return error;
        }

        var isActive = request.IsActive ?? type.IsActive;
        if (type.IsActive && !isActive)
        {
            // A machine still in use keeps its type in the picker: editing it would
            // otherwise show an empty type and refuse to save. Retired machines
            // (condemned, disposed) do not count.
            var inUse = await db.Equipment.CountAsync(
                e => e.EquipmentTypeId == id
                    && e.Status != EquipmentStatus.Condemned
                    && e.Status != EquipmentStatus.Disposed, ct);
            if (inUse > 0)
            {
                return Results.Conflict(new
                {
                    error = $"{inUse} machine{(inUse == 1 ? " is" : "s are")} still on the register as this type. " +
                            "Change their type or retire them first.",
                });
            }
        }

        // The code is not touched: the Excel import matches on it, and a checklist
        // or a saved spreadsheet that names it must keep working after a rename.
        type.Name = name;
        type.Description = Blank(request.Description);
        type.IsActive = isActive;

        // Two saves in one transaction. "One primary per type" is a unique index, and
        // moving the flag from one category to another in a single save can briefly
        // have two primaries, depending on the order the updates run in.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var wanted = request.CategoryIds.Distinct().ToHashSet();

        foreach (var link in type.Categories.Where(l => !wanted.Contains(l.CategoryId)).ToList())
        {
            db.EquipmentTypeCategories.Remove(link);
        }

        foreach (var link in type.Categories.Where(l => l.IsPrimary && l.CategoryId != request.PrimaryCategoryId))
        {
            link.IsPrimary = false;
        }

        await db.SaveChangesAsync(ct);

        foreach (var categoryId in wanted)
        {
            var link = type.Categories.FirstOrDefault(l => l.CategoryId == categoryId);
            if (link is null)
            {
                link = new EquipmentTypeCategory { CategoryId = categoryId };
                type.Categories.Add(link);
            }

            link.IsPrimary = categoryId == request.PrimaryCategoryId;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult?> ValidateAsync(
        EquipmentTypeRequest request, string name, int? existingId, HospitalPmDbContext db, CancellationToken ct)
    {
        if (name.Length == 0)
        {
            return Results.BadRequest(new { error = "Give the equipment type a name." });
        }

        if (name.Length > MaxNameLength)
        {
            return Results.BadRequest(new { error = $"The name can be at most {MaxNameLength} characters." });
        }

        var lowered = name.ToLower();
        if (await db.EquipmentTypes.AnyAsync(t => t.Id != existingId && t.Name.ToLower() == lowered, ct))
        {
            // Two types with the same name are indistinguishable in every picker.
            return Results.Conflict(new { error = $"There is already an equipment type called '{name}'." });
        }

        var categoryIds = request.CategoryIds?.Distinct().ToList() ?? [];
        if (categoryIds.Count == 0)
        {
            return Results.BadRequest(new { error = "Choose at least one category." });
        }

        var known = await db.Categories.CountAsync(c => categoryIds.Contains(c.Id) && c.IsActive, ct);
        if (known != categoryIds.Count)
        {
            return Results.BadRequest(new { error = "One of those categories does not exist." });
        }

        if (!categoryIds.Contains(request.PrimaryCategoryId))
        {
            return Results.BadRequest(new { error = "Choose which of the categories is the primary one." });
        }

        return null;
    }

    /// <summary>
    /// A stable machine key from the name ("Bedside monitor" becomes "bedside-monitor"),
    /// made unique by a number when a type already has it.
    /// </summary>
    private static async Task<string> NewCodeAsync(string name, HospitalPmDbContext db, CancellationToken ct)
    {
        var slug = Slug(name);
        var code = slug;

        for (var n = 2; await db.EquipmentTypes.AnyAsync(t => t.Code == code, ct); n++)
        {
            var suffix = $"-{n}";
            code = slug[..Math.Min(slug.Length, MaxCodeLength - suffix.Length)] + suffix;
        }

        return code;
    }

    private static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                sb.Append(ch);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0)
        {
            // A name with no letters or digits in it, for instance entirely in another script.
            slug = "type";
        }

        return slug.Length > MaxCodeLength ? slug[..MaxCodeLength].TrimEnd('-') : slug;
    }

    private static string? Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
