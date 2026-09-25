using HospitalPm.Api.Maintenance;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Infrastructure.Maintenance;
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

/// <summary>
/// The PM to set up for a machine as it is added: which checklist, how often, the date
/// the first one falls due, and how many days late counts as late. Only read when a
/// machine is created; changing a machine's PM later is done on the PM pages.
/// </summary>
public sealed record NewMachinePmRequest(
    int ChecklistTemplateId,
    PmFrequency Frequency,
    DateOnly FirstDueDate,
    int GraceDays = 7,
    // Who does it. The vendor only when this same request gives the machine a maintenance
    // contract (AMC or CMC).
    PmPerformedBy PerformedBy = PmPerformedBy.InHouse,
    // The dates, when the frequency is Manual: each one a PM of its own, picked by hand.
    // FirstDueDate is not used then.
    IReadOnlyList<DateOnly>? Dates = null);

public sealed record EquipmentRequest(
    string AssetTag,
    string? SerialNumber,
    int EquipmentTypeId,
    int LocationId,
    string? Manufacturer,
    string? Model,
    // Optional. Left out, a new machine is In use and an existing one keeps
    // the status it has. It used to be required by type only, so a request
    // without it arrived as 0 - a status that is not one of the five - and was
    // stored.
    EquipmentStatus? Status,
    DateOnly? PurchaseDate,
    DateOnly? InstallationDate,
    DateOnly? WarrantyExpiryDate,
    string? Notes,
    // Optional for the same reason as Status: an older client, or a spreadsheet
    // import, does not send it. A new machine is then left unclassified, and an
    // existing one keeps the level it has.
    EquipmentCriticality? Criticality = null,
    // Insurance. IsInsured left out means "do not touch": a new machine is not
    // insured and an existing one keeps its policy. False clears every detail.
    // True needs an insurer and an expiry date; the policy number is optional.
    bool? IsInsured = null,
    string? InsuranceProvider = null,
    string? InsurancePolicyNumber = null,
    DateOnly? InsuranceExpiryDate = null,
    // What the machine cost, and what its insurance costs, in rupees. Blank is
    // "not known". The insurance cost follows IsInsured like the other details.
    decimal? PurchaseCost = null,
    decimal? InsuranceCost = null,
    // A maintenance contract, AMC or CMC. HasMaintenanceContract left out means
    // "do not touch". False clears every detail. True needs the type, the vendor
    // and the start and end dates; the number and the cost are optional.
    bool? HasMaintenanceContract = null,
    MaintenanceContractType? MaintenanceContractType = null,
    string? MaintenanceVendor = null,
    string? MaintenanceContractNumber = null,
    DateOnly? MaintenanceStartDate = null,
    DateOnly? MaintenanceEndDate = null,
    decimal? MaintenanceCost = null,
    // Set up the machine's PM in the same request, so it is added and scheduled
    // together or not at all. Left out, no PM is scheduled.
    NewMachinePmRequest? Pm = null);

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
    string? Notes,
    EquipmentCriticality? Criticality,
    bool IsInsured,
    string? InsuranceProvider,
    string? InsurancePolicyNumber,
    DateOnly? InsuranceExpiryDate,
    decimal? PurchaseCost,
    decimal? InsuranceCost,
    MaintenanceContractType? MaintenanceContractType,
    string? MaintenanceVendor,
    string? MaintenanceContractNumber,
    DateOnly? MaintenanceStartDate,
    DateOnly? MaintenanceEndDate,
    decimal? MaintenanceCost);

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
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        group.MapPut("/{id:int}", UpdateAsync)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Retiring an asset is a register-owner decision, not an engineer's.
        group.MapPost("/{id:int}/condemn", CondemnAsync)
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }

    private static async Task<IResult> SearchAsync(
        HospitalPmDbContext db,
        [FromQuery] string? q,
        [FromQuery] int? locationId,
        [FromQuery] int? equipmentTypeId,
        [FromQuery] EquipmentStatus? status,
        [FromQuery] EquipmentCriticality? criticality,
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

        if (criticality is not null)
        {
            query = query.Where(e => e.Criticality == criticality);
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
                e.Notes,
                e.Criticality,
                e.IsInsured,
                e.InsuranceProvider,
                e.InsurancePolicyNumber,
                e.InsuranceExpiryDate,
                e.PurchaseCost,
                e.InsuranceCost,
                e.MaintenanceContractType,
                e.MaintenanceVendor,
                e.MaintenanceContractNumber,
                e.MaintenanceStartDate,
                e.MaintenanceEndDate,
                e.MaintenanceCost))
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
                e.Notes,
                e.Criticality,
                e.IsInsured,
                e.InsuranceProvider,
                e.InsurancePolicyNumber,
                e.InsuranceExpiryDate,
                e.PurchaseCost,
                e.InsuranceCost,
                e.MaintenanceContractType,
                e.MaintenanceVendor,
                e.MaintenanceContractNumber,
                e.MaintenanceStartDate,
                e.MaintenanceEndDate,
                e.MaintenanceCost))
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
                e.Notes,
                e.Criticality,
                e.IsInsured,
                e.InsuranceProvider,
                e.InsurancePolicyNumber,
                e.InsuranceExpiryDate,
                e.PurchaseCost,
                e.InsuranceCost,
                e.MaintenanceContractType,
                e.MaintenanceVendor,
                e.MaintenanceContractNumber,
                e.MaintenanceStartDate,
                e.MaintenanceEndDate,
                e.MaintenanceCost))
            .SingleOrDefaultAsync(ct);

        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] EquipmentRequest request,
        HospitalPmDbContext db,
        PmScheduleGenerator generator,
        HospitalClock clock,
        CancellationToken ct)
    {
        // Left blank, the software numbers the machine itself.
        var error = await ValidateAsync(request, db, ct, tagRequired: !string.IsNullOrWhiteSpace(request.AssetTag));
        if (error is not null)
        {
            return error;
        }

        // Checked before anything is written, so a PM that cannot be scheduled does not
        // leave the machine added without it.
        var pmError = await ValidatePmAsync(request, db, ct);
        if (pmError is not null)
        {
            return pmError;
        }

        string assetTag;
        if (string.IsNullOrWhiteSpace(request.AssetTag))
        {
            assetTag = await NextAssetTagAsync(db, ct);
        }
        else
        {
            assetTag = request.AssetTag.Trim();
            if (await db.Equipment.AnyAsync(e => e.AssetTag.ToLower() == assetTag.ToLower(), ct))
            {
                return Results.Conflict(new { error = $"Asset tag '{request.AssetTag}' is already in use." });
            }
        }

        var entity = new Domain.Assets.Equipment
        {
            AssetTag = assetTag,
            SerialNumber = request.SerialNumber?.Trim(),
            EquipmentTypeId = request.EquipmentTypeId,
            LocationId = request.LocationId,
            Manufacturer = request.Manufacturer?.Trim(),
            Model = request.Model?.Trim(),
            Status = request.Status ?? EquipmentStatus.InService,
            PurchaseDate = request.PurchaseDate,
            InstallationDate = request.InstallationDate,
            WarrantyExpiryDate = request.WarrantyExpiryDate,
            Notes = request.Notes,
            Criticality = request.Criticality,
            PurchaseCost = Money(request.PurchaseCost),
        };
        ApplyInsurance(entity, request);
        ApplyMaintenanceContract(entity, request);

        db.Equipment.Add(entity);

        if (request.Pm is { } pm)
        {
            var manual = pm.Frequency == PmFrequency.Manual;
            var picked = manual ? ManualPmDates.Clean(pm.Dates).Dates! : [];

            // One save, one transaction: the machine, its schedule and, for hand-picked dates,
            // every one of those PMs.
            var schedule = new PmSchedule
            {
                Equipment = entity,
                ChecklistTemplateId = pm.ChecklistTemplateId,
                Frequency = pm.Frequency,
                IntervalDays = 0,
                AnchorDate = manual ? picked[0] : pm.FirstDueDate,
                GraceDays = pm.GraceDays,
                PerformedBy = pm.PerformedBy,
            };
            db.PmSchedules.Add(schedule);

            if (manual)
            {
                db.PmTasks.AddRange(ManualPmDates.Tasks(schedule, entity, picked, clock.Today()));
            }
        }

        await db.SaveChangesAsync(ct);

        if (request.Pm is not null)
        {
            // Turned into due dates now, so the PM is on the work list straight away
            // rather than after tonight's job.
            await generator.RunAsync(ct);
        }

        return Results.Created($"/api/equipment/{entity.Id}", new { entity.Id, entity.AssetTag });
    }

    /// <summary>
    /// The next number in the hospital's own series, EQ-00001 and on.
    ///
    /// Drawn from a database sequence, so two people adding machines at once cannot be
    /// handed the same number. A number already used by a tag someone typed or imported
    /// is skipped rather than refused.
    /// </summary>
    private static async Task<string> NextAssetTagAsync(HospitalPmDbContext db, CancellationToken ct)
    {
        while (true)
        {
            var n = await db.Database
                .SqlQueryRaw<long>("SELECT nextval('equipment_asset_tag_seq') AS \"Value\"")
                .SingleAsync(ct);

            var tag = $"EQ-{n:D5}";
            if (!await db.Equipment.AnyAsync(e => e.AssetTag == tag, ct))
            {
                return tag;
            }
        }
    }

    private static async Task<IResult> UpdateAsync(
        int id,
        [FromBody] EquipmentRequest request,
        HospitalPmDbContext db,
        System.Security.Claims.ClaimsPrincipal principal,
        TimeProvider clock,
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
        // Changing the place here is a move like any other, and is written down as one.
        if (entity.LocationId != request.LocationId)
        {
            EquipmentMoveEndpoints.Record(
                db, entity, request.LocationId, "Changed on the machine's record",
                EquipmentMoveEndpoints.UserId(principal), clock.GetUtcNow().UtcDateTime);
        }

        entity.LocationId = request.LocationId;
        entity.Manufacturer = request.Manufacturer?.Trim();
        entity.Model = request.Model?.Trim();
        entity.Status = request.Status ?? entity.Status;
        entity.PurchaseDate = request.PurchaseDate;
        entity.InstallationDate = request.InstallationDate;
        entity.WarrantyExpiryDate = request.WarrantyExpiryDate;
        entity.Notes = request.Notes;
        entity.Criticality = request.Criticality ?? entity.Criticality;
        entity.PurchaseCost = Money(request.PurchaseCost);
        ApplyInsurance(entity, request);
        ApplyMaintenanceContract(entity, request);

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

    /// <summary>
    /// Says whether the machine is insured, and keeps the details consistent with the
    /// answer. "No" empties them, so a machine that is not insured never carries an
    /// old policy that would read as current.
    /// </summary>
    private static void ApplyInsurance(Domain.Assets.Equipment entity, EquipmentRequest request)
    {
        switch (request.IsInsured)
        {
            case null:
                return;

            case false:
                entity.IsInsured = false;
                entity.InsuranceProvider = null;
                entity.InsurancePolicyNumber = null;
                entity.InsuranceExpiryDate = null;
                entity.InsuranceCost = null;
                return;

            case true:
                entity.IsInsured = true;
                entity.InsuranceProvider = request.InsuranceProvider?.Trim();
                entity.InsurancePolicyNumber = string.IsNullOrWhiteSpace(request.InsurancePolicyNumber)
                    ? null
                    : request.InsurancePolicyNumber.Trim();
                entity.InsuranceExpiryDate = request.InsuranceExpiryDate;
                entity.InsuranceCost = Money(request.InsuranceCost);
                return;
        }
    }

    /// <summary>
    /// Says whether the machine is under an AMC or a CMC, and keeps the details
    /// consistent with the answer. "No" empties them, so a lapsed contract is not left
    /// behind on a machine that says it has none.
    /// </summary>
    private static void ApplyMaintenanceContract(Domain.Assets.Equipment entity, EquipmentRequest request)
    {
        switch (request.HasMaintenanceContract)
        {
            case null:
                return;

            case false:
                entity.MaintenanceContractType = null;
                entity.MaintenanceVendor = null;
                entity.MaintenanceContractNumber = null;
                entity.MaintenanceStartDate = null;
                entity.MaintenanceEndDate = null;
                entity.MaintenanceCost = null;
                return;

            case true:
                entity.MaintenanceContractType = request.MaintenanceContractType;
                entity.MaintenanceVendor = request.MaintenanceVendor?.Trim();
                entity.MaintenanceContractNumber = string.IsNullOrWhiteSpace(request.MaintenanceContractNumber)
                    ? null
                    : request.MaintenanceContractNumber.Trim();
                entity.MaintenanceStartDate = request.MaintenanceStartDate;
                entity.MaintenanceEndDate = request.MaintenanceEndDate;
                entity.MaintenanceCost = Money(request.MaintenanceCost);
                return;
        }
    }

    private static async Task<IResult?> ValidatePmAsync(
        EquipmentRequest request, HospitalPmDbContext db, CancellationToken ct)
    {
        var pm = request.Pm;
        var equipmentTypeId = request.EquipmentTypeId;

        if (pm is null)
        {
            return null;
        }

        if (!Enum.IsDefined(pm.PerformedBy))
        {
            return Results.BadRequest(new { error = "Say whether the hospital's team or the vendor does this PM." });
        }

        // The contract is given in this same request, so it is what is checked here.
        if (pm.PerformedBy == PmPerformedBy.Vendor
            && !(request.HasMaintenanceContract == true && request.MaintenanceContractType is not null))
        {
            return Results.BadRequest(new
            {
                error = "The vendor can only do the PM on a machine that has a maintenance contract (AMC or CMC).",
            });
        }

        if (pm.Frequency == PmFrequency.Manual)
        {
            // Dates picked one by one: there must be some, and they are what matters, not a first date.
            var (_, dateError) = ManualPmDates.Clean(pm.Dates);
            if (dateError is not null)
            {
                return Results.BadRequest(new { error = dateError });
            }
        }
        // Custom is left to the PM pages, which take an interval in days. This form is
        // for the named intervals and for dates picked one by one.
        else if (pm.Frequency.Months() is null)
        {
            return Results.BadRequest(new
            {
                error = "Choose how often: monthly, every 2 months, quarterly, half-yearly or yearly, or pick the dates yourself.",
            });
        }

        if (pm.GraceDays is < 0 or > 90)
        {
            return Results.BadRequest(new { error = "Grace days must be between 0 and 90." });
        }

        if (pm.Frequency != PmFrequency.Manual && pm.FirstDueDate.Year is < 2000 or > 2100)
        {
            return Results.BadRequest(new { error = "Give the date the first PM falls due." });
        }

        var template = await db.ChecklistTemplates
            .Where(t => t.Id == pm.ChecklistTemplateId && t.Kind == ChecklistKind.Pm)
            .Select(t => new { t.EquipmentTypeId })
            .SingleOrDefaultAsync(ct);

        if (template is null)
        {
            return Results.BadRequest(new { error = "Unknown checklist." });
        }

        // A ventilator checklist on an ultrasound is a technician being asked questions
        // that do not apply to the machine in front of them.
        if (template.EquipmentTypeId != equipmentTypeId)
        {
            return Results.BadRequest(new { error = "That checklist belongs to a different equipment type." });
        }

        return null;
    }

    /// <summary>The largest amount the column holds: twelve digits before the paise.</summary>
    private const decimal MaxCost = 999_999_999_999.99m;

    /// <summary>Whole paise, so 1234.567 is stored as 1234.57 rather than refused or silently cut.</summary>
    private static decimal? Money(decimal? amount) => amount is null ? null : Math.Round(amount.Value, 2);

    private static bool IsBadAmount(decimal? amount) => amount is < 0 or > MaxCost;

    private static async Task<IResult?> ValidateAsync(
        EquipmentRequest request, HospitalPmDbContext db, CancellationToken ct, bool tagRequired = true)
    {
        if (tagRequired && string.IsNullOrWhiteSpace(request.AssetTag))
        {
            return Results.BadRequest(new { error = "Asset tag is required." });
        }

        if (request.Status is { } status && !Enum.IsDefined(status))
        {
            return Results.BadRequest(new { error = "Unknown status." });
        }

        if (request.Criticality is { } criticality && !Enum.IsDefined(criticality))
        {
            return Results.BadRequest(new { error = "Unknown criticality." });
        }

        if (IsBadAmount(request.PurchaseCost))
        {
            return Results.BadRequest(new { error = "The cost of the machine cannot be negative." });
        }

        if (IsBadAmount(request.InsuranceCost))
        {
            return Results.BadRequest(new { error = "The cost of the insurance cannot be negative." });
        }

        if (IsBadAmount(request.MaintenanceCost))
        {
            return Results.BadRequest(new { error = "The cost of the maintenance contract cannot be negative." });
        }

        if (request.HasMaintenanceContract == true)
        {
            if (request.MaintenanceContractType is not { } contractType || !Enum.IsDefined(contractType))
            {
                return Results.BadRequest(new { error = "Say whether the maintenance contract is an AMC or a CMC." });
            }

            if (string.IsNullOrWhiteSpace(request.MaintenanceVendor))
            {
                return Results.BadRequest(new { error = "Say which vendor holds the maintenance contract." });
            }

            if (request.MaintenanceStartDate is null || request.MaintenanceEndDate is null)
            {
                return Results.BadRequest(new { error = "Say when the maintenance contract starts and ends." });
            }

            if (request.MaintenanceEndDate < request.MaintenanceStartDate)
            {
                return Results.BadRequest(new { error = "The maintenance contract cannot end before it starts." });
            }
        }

        if (request.IsInsured == true)
        {
            if (string.IsNullOrWhiteSpace(request.InsuranceProvider))
            {
                return Results.BadRequest(new { error = "Say which insurer covers this machine." });
            }

            if (request.InsuranceExpiryDate is null)
            {
                return Results.BadRequest(new { error = "Say when the insurance expires." });
            }
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
