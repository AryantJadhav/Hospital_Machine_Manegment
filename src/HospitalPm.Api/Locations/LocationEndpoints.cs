using HospitalPm.Api.Auth;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// See EquipmentEndpoints for why CA1862 is suppressed: EF Core cannot
// translate the StringComparison overloads these expressions would need.
#pragma warning disable CA1862

namespace HospitalPm.Api.Locations;

public sealed record LocationRequest(
    string Code,
    string Name,
    LocationLevel Level,
    int? ParentId);

/// <summary>How many beds to add to a room or a ward, and the number to start from (left out: the next free number).</summary>
public sealed record BedsRequest(int Count, int? StartAt);

public sealed record LocationResponse(
    int Id,
    string Code,
    string Name,
    LocationLevel Level,
    int? ParentId,
    int Depth,
    bool IsActive,
    int EquipmentCount,
    int ChildCount);

public static class LocationEndpoints
{
    public static void MapLocationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/locations")
            .WithTags("Locations")
            .RequirePermission(Permissions.RegisterView);

        // Every role reads the tree: a technician filtering the register by
        // department needs it as much as an admin does.
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:int}", GetAsync);

        // The location tree is the spine of the register. Getting it wrong
        // misfiles every asset under it, so editing is a register-owner job.
        group.MapPost("/", CreateAsync)
            .RequirePermission(Permissions.LocationsEdit);

        group.MapPut("/{id:int}", UpdateAsync)
            .RequirePermission(Permissions.LocationsEdit);

        group.MapDelete("/{id:int}", DeleteAsync)
            .RequirePermission(Permissions.LocationsEdit);

        // Beds for a room or a ward, a number at a time: "ICU Room 2 · Bed 1" to "Bed 8".
        group.MapPost("/{id:int}/beds", AddBedsAsync)
            .RequirePermission(Permissions.LocationsEdit);
    }

    private const int MaxBedsAtOnce = 200;

    /// <summary>
    /// Adds numbered beds to a room or a ward. Each bed is named with the room it is in, so wherever a machine's place is shown (a
    /// list, a label, a report) it reads "ICU Room 2 · Bed 3" and not just "Bed 3", which would mean nothing on its own.
    /// </summary>
    private static async Task<IResult> AddBedsAsync(
        int id, [FromBody] BedsRequest request, HospitalPmDbContext db, CancellationToken ct)
    {
        var parent = await db.Locations.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, ct);
        if (parent is null)
        {
            return Results.NotFound();
        }

        if (parent.Level is not (LocationLevel.Room or LocationLevel.Department))
        {
            return Results.BadRequest(new { error = "Beds go in a room or in a ward (a department). Choose one of those." });
        }

        if (request.Count is < 1 or > MaxBedsAtOnce)
        {
            return Results.BadRequest(new { error = $"Add between 1 and {MaxBedsAtOnce} beds at a time." });
        }

        if (request.StartAt is < 1 or > 9999)
        {
            return Results.BadRequest(new { error = "Start from a bed number between 1 and 9999." });
        }

        // The next free number: after the highest "Bed n" already in this room or ward.
        var existing = await db.Locations.AsNoTracking()
            .Where(l => l.ParentId == id && l.Level == LocationLevel.Bed)
            .Select(l => l.Name)
            .ToListAsync(ct);

        var highest = existing
            .Select(n => System.Text.RegularExpressions.Regex.Match(n, @"Bed (\d+)\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max();

        var first = request.StartAt ?? highest + 1;
        var numbers = Enumerable.Range(first, request.Count).ToList();

        // The room's name goes in the bed's name, and its code in the bed's code: both are limited, so say so rather than cut them.
        var parentName = parent.Name.Length > 180 ? parent.Name[..180] : parent.Name;
        if (parent.Code.Length > 56)
        {
            return Results.BadRequest(new { error = "This place's code is too long to add numbered beds under. Shorten its code first." });
        }

        var codes = numbers.Select(n => $"{parent.Code}-B{n:00}").ToList();
        var taken = await db.Locations.AsNoTracking().Where(l => codes.Contains(l.Code)).Select(l => l.Code).ToListAsync(ct);
        if (taken.Count > 0)
        {
            return Results.Conflict(new { error = $"A place with the code {taken[0]} already exists, so those bed numbers are taken. Start from a later number." });
        }

        var beds = numbers
            .Select(n => new Location { Code = $"{parent.Code}-B{n:00}", Name = $"{parentName} · Bed {n}", Level = LocationLevel.Bed, ParentId = id })
            .ToList();

        db.Locations.AddRange(beds);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            return TranslateAsync(pg);
        }

        return Results.Created($"/api/locations/{id}", new { added = beds.Count, ids = beds.Select(b => b.Id), names = beds.Select(b => b.Name) });
    }

    private static async Task<IResult> ListAsync(HospitalPmDbContext db, CancellationToken ct)
    {
        // Ordered by path so the caller receives the tree already in
        // depth-first order and can render it without sorting.
        var items = await db.Locations
            .AsNoTracking()
            .OrderBy(l => l.Path)
            .Select(l => new LocationResponse(
                l.Id, l.Code, l.Name, l.Level, l.ParentId, l.Depth, l.IsActive,
                db.Equipment.Count(e => e.LocationId == l.Id),
                db.Locations.Count(c => c.ParentId == l.Id)))
            .ToListAsync(ct);

        return Results.Ok(items);
    }

    private static async Task<IResult> GetAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        var item = await db.Locations
            .AsNoTracking()
            .Where(l => l.Id == id)
            .Select(l => new LocationResponse(
                l.Id, l.Code, l.Name, l.Level, l.ParentId, l.Depth, l.IsActive,
                db.Equipment.Count(e => e.LocationId == l.Id),
                db.Locations.Count(c => c.ParentId == l.Id)))
            .SingleOrDefaultAsync(ct);

        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] LocationRequest request, HospitalPmDbContext db, CancellationToken ct)
    {
        var error = await ValidateAsync(request, null, db, ct);
        if (error is not null)
        {
            return error;
        }

        var entity = new Location
        {
            Code = request.Code.Trim(),
            Name = request.Name.Trim(),
            Level = request.Level,
            ParentId = request.ParentId,
        };

        db.Locations.Add(entity);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            return TranslateAsync(pg);
        }

        return Results.Created($"/api/locations/{entity.Id}", new { entity.Id });
    }

    private static async Task<IResult> UpdateAsync(
        int id, [FromBody] LocationRequest request, HospitalPmDbContext db, CancellationToken ct)
    {
        var entity = await db.Locations.SingleOrDefaultAsync(l => l.Id == id, ct);
        if (entity is null)
        {
            return Results.NotFound();
        }

        var error = await ValidateAsync(request, id, db, ct);
        if (error is not null)
        {
            return error;
        }

        entity.Code = request.Code.Trim();
        entity.Name = request.Name.Trim();
        entity.Level = request.Level;
        entity.ParentId = request.ParentId;

        try
        {
            // Re-parenting rewrites every descendant's path, done by trigger.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            return TranslateAsync(pg);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Removes a location, but only a genuinely empty one.
    ///
    /// Deleting a location that still holds equipment or child locations
    /// would either orphan assets or silently take a whole subtree with it.
    /// The database refuses it either way; this returns a message that says
    /// what is in the way instead of a foreign-key error.
    /// </summary>
    private static async Task<IResult> DeleteAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        var entity = await db.Locations.SingleOrDefaultAsync(l => l.Id == id, ct);
        if (entity is null)
        {
            return Results.NotFound();
        }

        var equipmentCount = await db.Equipment.CountAsync(e => e.LocationId == id, ct);
        var childCount = await db.Locations.CountAsync(l => l.ParentId == id, ct);

        if (equipmentCount > 0 || childCount > 0)
        {
            return Results.Conflict(new
            {
                error = BuildInUseMessage(equipmentCount, childCount),
                equipmentCount,
                childCount,
            });
        }

        db.Locations.Remove(entity);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static string BuildInUseMessage(int equipmentCount, int childCount)
    {
        var parts = new List<string>(2);
        if (equipmentCount > 0)
        {
            parts.Add($"{equipmentCount} asset{(equipmentCount == 1 ? "" : "s")}");
        }
        if (childCount > 0)
        {
            parts.Add($"{childCount} sub-location{(childCount == 1 ? "" : "s")}");
        }

        return $"This location still holds {string.Join(" and ", parts)}. " +
               "Move or remove them first, or mark the location inactive instead of deleting it.";
    }

    private static async Task<IResult?> ValidateAsync(
        LocationRequest request, int? existingId, HospitalPmDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            return Results.BadRequest(new { error = "Code is required." });
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.BadRequest(new { error = "Name is required." });
        }

        if (!Enum.IsDefined(request.Level))
        {
            return Results.BadRequest(new { error = "Unknown location level." });
        }

        var code = request.Code.Trim();
        var clash = await db.Locations
            .AnyAsync(l => l.Code.ToLower() == code.ToLower() && l.Id != existingId, ct);

        if (clash)
        {
            return Results.Conflict(new { error = $"Code '{code}' is already used by another location." });
        }

        if (request.ParentId is null)
        {
            return null;
        }

        if (request.ParentId == existingId)
        {
            return Results.BadRequest(new { error = "A location cannot be its own parent." });
        }

        var parent = await db.Locations
            .Where(l => l.Id == request.ParentId)
            .Select(l => new { l.Level, l.Path })
            .SingleOrDefaultAsync(ct);

        if (parent is null)
        {
            return Results.BadRequest(new { error = "The parent location does not exist." });
        }

        // Mirrors the database trigger so the caller gets a clear 400 rather
        // than a 500 carrying a raised Postgres exception.
        if ((int)request.Level <= (int)parent.Level)
        {
            return Results.BadRequest(new
            {
                error = $"A {request.Level} cannot sit inside a {parent.Level}. " +
                        "Levels may be skipped, but a child must be deeper than its parent.",
            });
        }

        if (existingId is not null && parent.Path.Contains($"/{existingId}/", StringComparison.Ordinal))
        {
            return Results.BadRequest(new
            {
                error = "That would move the location inside one of its own descendants.",
            });
        }

        return null;
    }

    /// <summary>
    /// Turns the trigger and constraint violations into messages an operator
    /// can act on. These are the same rules validated above; this catches the
    /// race where another request changes the tree in between.
    /// </summary>
    private static IResult TranslateAsync(PostgresException pg) => pg.SqlState switch
    {
        "23505" => Results.Conflict(new { error = "That code is already used by another location." }),
        "23503" => Results.BadRequest(new { error = "The parent location does not exist." }),
        _ when pg.MessageText.Contains("its own ancestor", StringComparison.OrdinalIgnoreCase)
            => Results.BadRequest(new { error = "That would move the location inside one of its own descendants." }),
        _ when pg.MessageText.Contains("must be deeper", StringComparison.OrdinalIgnoreCase)
            => Results.BadRequest(new { error = "A child location must sit at a deeper level than its parent." }),
        // Too long, a character that cannot be stored, and the like: the same plain replies as everywhere else.
        _ when Hosting.DatabaseErrorMiddleware.Describe(pg) is { } refused
            => Results.Json(new { error = refused.Message }, statusCode: refused.Status),
        _ => Results.Problem("The location could not be saved."),
    };
}
