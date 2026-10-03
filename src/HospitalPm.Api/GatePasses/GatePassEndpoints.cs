using System.Globalization;
using System.Security.Claims;
using HospitalPm.Api.Auth;
using HospitalPm.Api.Equipment;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.GatePasses;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using HospitalPm.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using QuestPDF.Fluent;

// See EquipmentEndpoints: these expressions are LINQ-to-SQL, where ToLower() is
// what translates and the StringComparison overloads CA1862 asks for do not.
#pragma warning disable CA1862

namespace HospitalPm.Api.GatePasses;

/// <summary>
/// One line of a gate pass. Give <see cref="EquipmentId"/> for a registered machine and its description and
/// number are filled in from its record; leave it out for anything else (a cable, a probe, accessories).
/// </summary>
public sealed record GatePassItemRequest(int? EquipmentId, string? Description, string? AssetCode, int? Quantity, string? Remarks);

public sealed record GatePassRequest(
    // Left out, the pass is dated today.
    DateOnly? PassDate,
    string? VendorName,
    string? ContactPerson,
    string? ContactPhone,
    // Left out, "Sending to the company for repair".
    string? Purpose,
    // The service request this repair is for (the form's Request No.). Optional.
    int? WorkOrderId,
    DateOnly? ExpectedReturnDate,
    string? AuthorisedBy,
    string? Notes,
    // The whole list. On an edit it replaces what was there.
    List<GatePassItemRequest>? Items);

public sealed record GatePassReturnRequest(
    // Left out, the day it is recorded.
    DateOnly? ReturnedOn,
    string? Notes);

public sealed record GatePassCancelRequest(string? Notes);

/// <summary>
/// Returnable gate passes: the record of machines that left the hospital for a vendor to repair.
///
/// A machine that cannot be repaired where it stands goes to the company on a gate pass. Everyone who
/// works on the equipment can read them, so anyone can find out where a machine is and when it is due
/// back; engineers write them, as they do the sending. Passes are never deleted: a number that was
/// issued is a number that was issued, so a pass written in error is cancelled and stays on the list.
///
/// Nothing here is about a patient. See CLAUDE.md.
/// </summary>
public static class GatePassEndpoints
{
    private const int MaxPageSize = 200;
    private const int MaxTextLength = 200;
    private const int MaxDescriptionLength = 300;
    private const int MaxRemarksLength = 500;
    private const int MaxNotesLength = 4000;
    private const int MaxItems = 100;
    private const int MaxQuantity = 100_000;

    /// <summary>What a pass says it is for when nobody said.</summary>
    public const string DefaultPurpose = "Sending to the company for repair";

    public static void MapGatePassEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/gate-passes").WithTags("Gate passes").RequirePermission(Permissions.GatePassView);

        group.MapGet("/", ListAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapGet("/{id:int}/pdf", PdfAsync);

        group.MapPost("/", CreateAsync).RequirePermission(Permissions.GatePassEdit);
        group.MapPut("/{id:int}", UpdateAsync).RequirePermission(Permissions.GatePassEdit);
        group.MapPost("/{id:int}/return", ReturnAsync).RequirePermission(Permissions.GatePassEdit);
        group.MapPost("/{id:int}/cancel", CancelAsync).RequirePermission(Permissions.GatePassEdit);
    }

    // ---------------------------------------------------------------- reading

    private static async Task<IResult> ListAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        [FromQuery] string? q,
        [FromQuery] string? status,
        [FromQuery] int? equipmentId,
        [FromQuery] int? workOrderId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var today = clock.Today();

        if (!string.IsNullOrWhiteSpace(status) && StatusFilter(status) is null)
        {
            return Results.BadRequest(new { error = "The status must be out, overdue, returned or cancelled." });
        }

        var query = db.GatePasses.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            // "GP-1001", "gp1001" and "1001" all find pass 1001.
            var digits = term.StartsWith("gp", StringComparison.Ordinal) ? term[2..].TrimStart('-', ' ') : term;
            var number = int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;

            query = query.Where(p =>
                p.Number == number ||
                p.VendorName.ToLower().Contains(term) ||
                (p.ContactPerson != null && p.ContactPerson.ToLower().Contains(term)) ||
                p.Purpose.ToLower().Contains(term) ||
                (p.WorkOrder != null && p.WorkOrder.Number.ToLower().Contains(term)) ||
                p.Items.Any(i =>
                    i.Description.ToLower().Contains(term) ||
                    (i.AssetCode != null && i.AssetCode.ToLower().Contains(term))));
        }

        if (equipmentId is not null)
        {
            query = query.Where(p => p.Items.Any(i => i.EquipmentId == equipmentId));
        }

        if (workOrderId is not null)
        {
            query = query.Where(p => p.WorkOrderId == workOrderId);
        }

        if (from is not null)
        {
            query = query.Where(p => p.PassDate >= from);
        }

        if (to is not null)
        {
            query = query.Where(p => p.PassDate <= to);
        }

        // The tabs' numbers follow the search but not the tab chosen, so each says what it would show.
        var counts = new
        {
            Out = await query.CountAsync(p => p.Status == GatePassStatus.Out, ct),
            Overdue = await query.CountAsync(p => p.Status == GatePassStatus.Out && p.ExpectedReturnDate != null && p.ExpectedReturnDate < today, ct),
            Returned = await query.CountAsync(p => p.Status == GatePassStatus.Returned, ct),
            Cancelled = await query.CountAsync(p => p.Status == GatePassStatus.Cancelled, ct),
        };

        query = StatusFilter(status) switch
        {
            "out" => query.Where(p => p.Status == GatePassStatus.Out),
            "overdue" => query.Where(p => p.Status == GatePassStatus.Out && p.ExpectedReturnDate != null && p.ExpectedReturnDate < today),
            "returned" => query.Where(p => p.Status == GatePassStatus.Returned),
            "cancelled" => query.Where(p => p.Status == GatePassStatus.Cancelled),
            _ => query,
        };

        var total = await query.CountAsync(ct);

        var rows = await query
            // What is still out comes first, the longest overdue at the top; then the rest, newest first.
            .OrderBy(p => p.Status == GatePassStatus.Out ? 0 : 1)
            .ThenBy(p => p.Status == GatePassStatus.Out ? p.ExpectedReturnDate : null)
            .ThenByDescending(p => p.PassDate)
            .ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new
            {
                p.Id,
                p.Number,
                p.PassDate,
                p.VendorName,
                p.ContactPerson,
                p.Purpose,
                p.Status,
                p.ExpectedReturnDate,
                p.ReturnedOn,
                WorkOrderId = p.WorkOrderId,
                WorkOrderNumber = p.WorkOrder == null ? null : p.WorkOrder.Number,
                ItemCount = p.Items.Count,
                TotalQuantity = p.Items.Sum(i => (int?)i.Quantity) ?? 0,
                SomeItems = p.Items.OrderBy(i => i.Id).Select(i => i.Description).Take(3).ToList(),
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new
        {
            r.Id,
            r.Number,
            Reference = GatePass.ReferenceFor(r.Number),
            r.PassDate,
            r.VendorName,
            r.ContactPerson,
            r.Purpose,
            Status = StatusName(r.Status),
            r.ExpectedReturnDate,
            r.ReturnedOn,
            IsOverdue = IsOverdue(r.Status, r.ExpectedReturnDate, today),
            DaysOut = DaysOut(r.Status, r.PassDate, r.ReturnedOn, today),
            r.WorkOrderId,
            r.WorkOrderNumber,
            r.ItemCount,
            r.TotalQuantity,
            r.SomeItems,
        });

        return Results.Ok(new { items, total, page, pageSize, counts });
    }

    private static async Task<IResult> GetAsync(int id, HospitalPmDbContext db, HospitalClock clock, CancellationToken ct)
    {
        var p = await db.GatePasses.AsNoTracking()
            .Include(x => x.WorkOrder)
            .Include(x => x.Items).ThenInclude(i => i.Machine!).ThenInclude(m => m.EquipmentType)
            .Include(x => x.Items).ThenInclude(i => i.Machine!).ThenInclude(m => m.Location)
            .SingleOrDefaultAsync(x => x.Id == id, ct);

        if (p is null)
        {
            return Results.NotFound();
        }

        var today = clock.Today();
        var createdBy = await db.Users.AsNoTracking()
            .Where(u => u.Id == p.CreatedByUserId).Select(u => u.FullName).FirstOrDefaultAsync(ct);

        return Results.Ok(new
        {
            p.Id,
            p.Number,
            p.Reference,
            p.PassDate,
            p.VendorName,
            p.ContactPerson,
            p.ContactPhone,
            p.Purpose,
            Status = StatusName(p.Status),
            p.ExpectedReturnDate,
            p.ReturnedOn,
            IsOverdue = IsOverdue(p.Status, p.ExpectedReturnDate, today),
            DaysOut = DaysOut(p.Status, p.PassDate, p.ReturnedOn, today),
            p.AuthorisedBy,
            p.Notes,
            p.OutcomeNotes,
            WorkOrder = p.WorkOrder is null
                ? null
                : new { p.WorkOrder.Id, p.WorkOrder.Number, ReportedOn = HospitalDay(p.WorkOrder.ReportedAtUtc, clock) },
            TotalQuantity = p.Items.Sum(i => i.Quantity),
            Items = p.Items.OrderBy(i => i.Id).Select(i => new
            {
                i.Id,
                i.EquipmentId,
                i.Description,
                i.AssetCode,
                i.Quantity,
                i.Remarks,
                // The machine's own record, for the link and the place it was taken from.
                Machine = i.Machine is null
                    ? null
                    : new { i.Machine.Id, i.Machine.AssetTag, MachineName = i.Machine.EquipmentType?.Name, LocationName = i.Machine.Location?.Name },
            }).ToList(),
            CreatedByName = createdBy,
            p.CreatedAtUtc,
        });
    }

    private static async Task<IResult> PdfAsync(
        int id,
        HospitalPmDbContext db,
        HospitalClock clock,
        IOptions<ReportOptions> options,
        CancellationToken ct)
    {
        var p = await db.GatePasses.AsNoTracking()
            .Include(x => x.WorkOrder)
            .Include(x => x.Items)
            .SingleOrDefaultAsync(x => x.Id == id, ct);

        if (p is null)
        {
            return Results.NotFound();
        }

        var preparedBy = await db.Users.AsNoTracking()
            .Where(u => u.Id == p.CreatedByUserId).Select(u => u.FullName).FirstOrDefaultAsync(ct);

        var data = new GatePassReportData(
            p.Number,
            p.PassDate,
            p.VendorName,
            p.ContactPerson,
            p.ContactPhone,
            p.Purpose,
            p.WorkOrder?.Number,
            p.WorkOrder is null ? null : HospitalDay(p.WorkOrder.ReportedAtUtc, clock),
            p.ExpectedReturnDate,
            p.ReturnedOn,
            p.Status == GatePassStatus.Cancelled,
            p.AuthorisedBy,
            preparedBy,
            p.Items.OrderBy(i => i.Id).Select(i => new GatePassReportItem(i.Description, i.AssetCode, i.Quantity, i.Remarks)).ToList());

        var pdf = new GatePassDocument(data, options.Value).GeneratePdf();

        return Results.File(pdf, "application/pdf", $"{data.Reference}.pdf");
    }

    // ---------------------------------------------------------------- writing

    private static async Task<IResult> CreateAsync(
        [FromBody] GatePassRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var (items, error) = await ValidateAsync(request, null, db, clock, ct);
        if (error is not null)
        {
            return error;
        }

        var pass = new GatePass
        {
            PassDate = request.PassDate ?? clock.Today(),
            VendorName = request.VendorName!.Trim(),
            ContactPerson = Blank(request.ContactPerson),
            ContactPhone = Blank(request.ContactPhone),
            Purpose = Blank(request.Purpose) ?? DefaultPurpose,
            WorkOrderId = request.WorkOrderId,
            ExpectedReturnDate = request.ExpectedReturnDate,
            AuthorisedBy = Blank(request.AuthorisedBy),
            Notes = Blank(request.Notes),
            Status = GatePassStatus.Out,
            CreatedByUserId = EquipmentMoveEndpoints.UserId(principal),
            Items = items,
        };

        // Out of the hospital is out of service: the register says Under repair for as long as it is away.
        await TakeOutAsync(db, items, ct);

        db.GatePasses.Add(pass);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/gate-passes/{pass.Id}", new { pass.Id, pass.Number, pass.Reference });
    }

    private static async Task<IResult> UpdateAsync(
        int id,
        [FromBody] GatePassRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        CancellationToken ct)
    {
        var pass = await db.GatePasses.Include(p => p.Items).SingleOrDefaultAsync(p => p.Id == id, ct);
        if (pass is null)
        {
            return Results.NotFound();
        }

        if (pass.Status != GatePassStatus.Out)
        {
            return Results.Conflict(new { error = $"{pass.Reference} has been {StatusName(pass.Status).ToLowerInvariant()} and can no longer be changed." });
        }

        var (items, error) = await ValidateAsync(request, id, db, clock, ct);
        if (error is not null)
        {
            return error;
        }

        pass.PassDate = request.PassDate ?? pass.PassDate;
        pass.VendorName = request.VendorName!.Trim();
        pass.ContactPerson = Blank(request.ContactPerson);
        pass.ContactPhone = Blank(request.ContactPhone);
        pass.Purpose = Blank(request.Purpose) ?? DefaultPurpose;
        pass.WorkOrderId = request.WorkOrderId;
        pass.ExpectedReturnDate = request.ExpectedReturnDate;
        pass.AuthorisedBy = Blank(request.AuthorisedBy);
        pass.Notes = Blank(request.Notes);
        pass.UpdatedAtUtc = DateTime.UtcNow;

        // A machine still on the pass keeps what it was before it went out; one added goes out now; one taken
        // off was never going, so it is put back as it was.
        var before = pass.Items.Where(i => i.EquipmentId != null).ToDictionary(i => i.EquipmentId!.Value);
        foreach (var kept in items.Where(i => i.EquipmentId is { } id && before.ContainsKey(id)))
        {
            kept.EquipmentStatusBefore = before[kept.EquipmentId!.Value].EquipmentStatusBefore;
        }

        await TakeOutAsync(db, items.Where(i => i.EquipmentId is { } id && !before.ContainsKey(id)).ToList(), ct);
        var keptIds = items.Where(i => i.EquipmentId != null).Select(i => i.EquipmentId!.Value).ToHashSet();
        await BringBackAsync(db, pass.Items.Where(i => i.EquipmentId is { } id && !keptIds.Contains(id)).ToList(), ct);

        // The list as sent replaces the list as it was, in the one save, so a failure leaves the old one.
        db.GatePassItems.RemoveRange(pass.Items);
        pass.Items = items;

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ReturnAsync(
        int id,
        [FromBody] GatePassReturnRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        CancellationToken ct)
    {
        var pass = await db.GatePasses.Include(p => p.Items).SingleOrDefaultAsync(p => p.Id == id, ct);
        if (pass is null)
        {
            return Results.NotFound();
        }

        if (pass.Status != GatePassStatus.Out)
        {
            return Results.Conflict(new { error = $"{pass.Reference} is already {StatusName(pass.Status).ToLowerInvariant()}." });
        }

        var today = clock.Today();
        var day = request.ReturnedOn ?? today;

        if (day > today)
        {
            return Results.BadRequest(new { error = "The day it came back cannot be a day that has not happened yet." });
        }

        if (day < pass.PassDate)
        {
            return Results.BadRequest(new { error = $"It cannot have come back before the pass was written, on {pass.PassDate:dd/MM/yyyy}." });
        }

        if (request.Notes?.Length > MaxNotesLength)
        {
            return Results.BadRequest(new { error = $"The notes can be at most {MaxNotesLength} characters." });
        }

        pass.Status = GatePassStatus.Returned;
        pass.ReturnedOn = day;
        pass.OutcomeNotes = Blank(request.Notes);
        pass.UpdatedAtUtc = DateTime.UtcNow;

        await BringBackAsync(db, pass.Items.ToList(), ct);

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> CancelAsync(
        int id,
        [FromBody] GatePassCancelRequest request,
        HospitalPmDbContext db,
        CancellationToken ct)
    {
        var pass = await db.GatePasses.Include(p => p.Items).SingleOrDefaultAsync(p => p.Id == id, ct);
        if (pass is null)
        {
            return Results.NotFound();
        }

        if (pass.Status != GatePassStatus.Out)
        {
            return Results.Conflict(new { error = $"{pass.Reference} is already {StatusName(pass.Status).ToLowerInvariant()}." });
        }

        if (request.Notes?.Length > MaxNotesLength)
        {
            return Results.BadRequest(new { error = $"The notes can be at most {MaxNotesLength} characters." });
        }

        pass.Status = GatePassStatus.Cancelled;
        pass.OutcomeNotes = Blank(request.Notes);
        pass.UpdatedAtUtc = DateTime.UtcNow;

        // It never left, so the machine is put back as it was.
        await BringBackAsync(db, pass.Items.ToList(), ct);

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ---------------------------------------------------------------- the register

    /// <summary>
    /// A machine that goes out is Under repair on the register until it is back. What it was is kept on the line, so
    /// it can be put back as it was. A machine already condemned or disposed of is left alone: sending it out does not
    /// bring it back into the register's working stock.
    /// </summary>
    private static async Task TakeOutAsync(HospitalPmDbContext db, List<GatePassItem> items, CancellationToken ct)
    {
        var ids = items.Where(i => i.EquipmentId != null).Select(i => i.EquipmentId!.Value).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var machines = await db.Equipment.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        foreach (var item in items.Where(i => i.EquipmentId != null))
        {
            var machine = machines[item.EquipmentId!.Value];
            item.EquipmentStatusBefore = machine.Status;

            if (machine.Status is not (EquipmentStatus.Condemned or EquipmentStatus.Disposed))
            {
                machine.Status = EquipmentStatus.UnderRepair;
            }
        }
    }

    /// <summary>
    /// Puts machines back when their pass is returned, cancelled, or the machine is taken off it. Only a machine that
    /// is still Under repair is touched: if someone has since condemned it, or set its status by hand, that is a
    /// decision this does not undo. One that was in store goes back to store; anything else goes back in use.
    /// </summary>
    private static async Task BringBackAsync(HospitalPmDbContext db, List<GatePassItem> items, CancellationToken ct)
    {
        var ids = items.Where(i => i.EquipmentId != null).Select(i => i.EquipmentId!.Value).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var machines = await db.Equipment.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
        foreach (var item in items.Where(i => i.EquipmentId != null))
        {
            var machine = machines[item.EquipmentId!.Value];
            if (machine.Status == EquipmentStatus.UnderRepair)
            {
                machine.Status = item.EquipmentStatusBefore == EquipmentStatus.InStore
                    ? EquipmentStatus.InStore
                    : EquipmentStatus.InService;
            }
        }
    }

    // ---------------------------------------------------------------- rules

    private static async Task<(List<GatePassItem> Items, IResult? Error)> ValidateAsync(
        GatePassRequest request, int? thisPassId, HospitalPmDbContext db, HospitalClock clock, CancellationToken ct)
    {
        List<GatePassItem> none = [];
        static (List<GatePassItem>, IResult?) Bad(string message) => ([], Results.BadRequest(new { error = message }));

        var vendor = request.VendorName?.Trim() ?? string.Empty;
        if (vendor.Length == 0)
        {
            return Bad("Say which company or person the goods are going to.");
        }

        if (vendor.Length > MaxTextLength || request.ContactPerson?.Trim().Length > MaxTextLength
            || request.Purpose?.Trim().Length > MaxTextLength || request.AuthorisedBy?.Trim().Length > MaxTextLength)
        {
            return Bad($"The company, the contact, the purpose and the authoriser can each be at most {MaxTextLength} characters.");
        }

        if (request.ContactPhone?.Trim().Length > 50)
        {
            return Bad("The phone number can be at most 50 characters.");
        }

        if (request.Notes?.Length > MaxNotesLength)
        {
            return Bad($"The notes can be at most {MaxNotesLength} characters.");
        }

        var today = clock.Today();
        var passDate = request.PassDate ?? today;
        if (passDate.Year < 2000 || passDate > today)
        {
            return Bad("Give the day the pass was written. It cannot be a day that has not come yet.");
        }

        if (request.ExpectedReturnDate is { } due && (due < passDate || due > passDate.AddYears(5)))
        {
            return Bad("The day it is expected back cannot be before the pass was written, or more than five years after.");
        }

        if (request.WorkOrderId is { } workOrderId && !await db.WorkOrders.AnyAsync(w => w.Id == workOrderId, ct))
        {
            return Bad("Unknown service request.");
        }

        var requested = request.Items ?? [];
        if (requested.Count == 0)
        {
            return Bad("Add at least one item to send out.");
        }

        if (requested.Count > MaxItems)
        {
            return Bad($"A gate pass can list at most {MaxItems} items.");
        }

        var machineIds = requested.Where(i => i.EquipmentId is not null).Select(i => i.EquipmentId!.Value).ToList();
        if (machineIds.Distinct().Count() != machineIds.Count)
        {
            return Bad("The same machine is listed twice. Take one of the lines out.");
        }

        var machines = await db.Equipment.AsNoTracking()
            .Include(e => e.EquipmentType)
            .Where(e => machineIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, ct);

        // A machine is in one place. If it is already out on another pass it has not come back to go out again.
        var alreadyOut = await db.GatePassItems.AsNoTracking()
            .Where(i => i.EquipmentId != null && machineIds.Contains(i.EquipmentId.Value)
                && i.GatePass!.Status == GatePassStatus.Out && i.GatePassId != thisPassId)
            .Select(i => new { i.EquipmentId, i.GatePass!.Number })
            .ToListAsync(ct);

        var items = new List<GatePassItem>();
        foreach (var line in requested)
        {
            string description;
            string? assetCode = Blank(line.AssetCode);

            if (line.EquipmentId is { } machineId)
            {
                if (!machines.TryGetValue(machineId, out var machine))
                {
                    return Bad("One of the machines chosen is not on the register.");
                }

                if (alreadyOut.FirstOrDefault(o => o.EquipmentId == machineId) is { } out1)
                {
                    return Bad($"{machine.AssetTag} is already out on gate pass {GatePass.ReferenceFor(out1.Number)}. Record that it came back before sending it out again.");
                }

                if (line.Quantity is > 1)
                {
                    return Bad($"{machine.AssetTag} is one machine, so the quantity is 1.");
                }

                // What the register says, unless the engineer wrote something more exact.
                description = Blank(line.Description) ?? DescribeMachine(machine);
                assetCode ??= machine.AssetTag;
            }
            else
            {
                description = Blank(line.Description) ?? string.Empty;
                if (description.Length == 0)
                {
                    return Bad("Every line needs a description, or a machine chosen from the register.");
                }
            }

            if (description.Length > MaxDescriptionLength)
            {
                return Bad($"A description can be at most {MaxDescriptionLength} characters.");
            }

            if (assetCode?.Length > 100 || line.Remarks?.Trim().Length > MaxRemarksLength)
            {
                return Bad($"An asset code can be at most 100 characters, and remarks at most {MaxRemarksLength}.");
            }

            var quantity = line.Quantity ?? 1;
            if (quantity < 1 || quantity > MaxQuantity)
            {
                return Bad("The quantity on each line must be at least 1.");
            }

            items.Add(new GatePassItem
            {
                EquipmentId = line.EquipmentId,
                Description = description,
                AssetCode = assetCode,
                Quantity = quantity,
                Remarks = Blank(line.Remarks),
            });
        }

        return (items, null);
    }

    /// <summary>"Ventilator - Hamilton C1, serial 12345": what a security guard can match against the machine in a trolley.</summary>
    private static string DescribeMachine(HospitalPm.Domain.Assets.Equipment m)
    {
        var parts = new List<string>();
        var name = m.EquipmentType?.Name;
        var make = string.Join(' ', new[] { m.Manufacturer, m.Model }.Where(s => !string.IsNullOrWhiteSpace(s)));

        if (!string.IsNullOrWhiteSpace(name))
        {
            parts.Add(name);
        }

        if (make.Length > 0)
        {
            parts.Add(make);
        }

        var text = string.Join(" - ", parts);
        if (!string.IsNullOrWhiteSpace(m.SerialNumber))
        {
            text = (text.Length > 0 ? text + ", " : string.Empty) + "serial " + m.SerialNumber.Trim();
        }

        text = text.Length > 0 ? text : m.AssetTag;
        return text.Length > MaxDescriptionLength ? text[..MaxDescriptionLength] : text;
    }

    // ---------------------------------------------------------------- small things

    private static string? StatusFilter(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" => string.Empty,
        "out" => "out",
        "overdue" => "overdue",
        "returned" => "returned",
        "cancelled" => "cancelled",
        _ => null,
    };

    private static string StatusName(GatePassStatus status) => status switch
    {
        GatePassStatus.Out => "Out",
        GatePassStatus.Returned => "Returned",
        _ => "Cancelled",
    };

    /// <summary>Still out, with a day it was due back that has gone by. Due today is not late yet.</summary>
    private static bool IsOverdue(GatePassStatus status, DateOnly? expected, DateOnly today) =>
        status == GatePassStatus.Out && expected is { } due && due < today;

    /// <summary>How many days it has been away: until today while it is out, until the day it came back after that. Null once cancelled.</summary>
    private static int? DaysOut(GatePassStatus status, DateOnly passDate, DateOnly? returnedOn, DateOnly today) => status switch
    {
        GatePassStatus.Out => Math.Max(0, today.DayNumber - passDate.DayNumber),
        GatePassStatus.Returned when returnedOn is { } back => Math.Max(0, back.DayNumber - passDate.DayNumber),
        _ => null,
    };

    private static DateOnly HospitalDay(DateTime utc, HospitalClock clock) =>
        DateOnly.FromDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc).Add(clock.Offset));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
