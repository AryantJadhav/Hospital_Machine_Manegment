using System.Globalization;
using System.Security.Claims;
using HospitalPm.Api.Auth;
using HospitalPm.Api.Equipment;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Incidents;
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

namespace HospitalPm.Api.Incidents;

public sealed record IncidentRequest(
    int EquipmentId,
    int Type,
    // Left out, the incident happened today.
    DateOnly? OccurredOn,
    // "14:30", when it is known.
    string? OccurredAt,
    string? Place,
    string? Description,
    string? InvolvedPerson,
    string? ImmediateAction,
    bool? TakenOutOfUse,
    // Left out, no visible damage.
    int? Damage);

/// <summary>What the biomedical team writes after looking into it. Fields left out are not changed.</summary>
public sealed record IncidentReviewRequest(string? Findings, string? CorrectiveAction, int? Damage);

/// <summary>
/// Incidents with machines: a drop, a fall, mishandling, a spill, a collision, a loss.
///
/// A department writes one up on the day, the biomedical team looks into it and says what caused it, and
/// the hospital keeps the record and can print it. A person from another department writes up and reads
/// the incidents on the machines of their own departments, and nothing outside them (see
/// HospitalPmDbContext.RestrictTo); the biomedical team sees all of them.
///
/// Nothing here is about a patient. It is about the machine. See CLAUDE.md.
/// </summary>
public static class IncidentEndpoints
{
    private const int MaxPageSize = 200;
    private const int MaxTextLength = 200;
    private const int MaxLongTextLength = 4000;

    public static void MapIncidentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/incidents").WithTags("Incidents").RequirePermission(Permissions.IncidentsView);

        group.MapGet("/", ListAsync);
        group.MapGet("/summary", SummaryAsync);
        group.MapGet("/report.pdf", SummaryPdfAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapGet("/{id:int}/report.pdf", PdfAsync);

        group.MapPost("/", ReportAsync).RequirePermission(Permissions.IncidentsReport);

        group.MapPut("/{id:int}", UpdateAsync).RequirePermission(Permissions.IncidentsManage);
        group.MapPut("/{id:int}/review", ReviewAsync).RequirePermission(Permissions.IncidentsManage);
        group.MapPost("/{id:int}/close", CloseAsync).RequirePermission(Permissions.IncidentsManage);
    }

    // ---------------------------------------------------------------- reading

    private static async Task<IResult> ListAsync(
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        [FromQuery] string? q,
        [FromQuery] string? status,
        [FromQuery] int? type,
        [FromQuery] int? equipmentId,
        [FromQuery] bool? mine,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        if (!string.IsNullOrWhiteSpace(status) && StatusFilter(status) is null)
        {
            return Results.BadRequest(new { error = "The status must be open, reported, review, closed or all." });
        }

        if (type is { } t && !Enum.IsDefined((IncidentType)t))
        {
            return Results.BadRequest(new { error = "Unknown kind of incident." });
        }

        var query = db.Incidents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            // "INC-2026-00012", "inc-2026-12" and "12" all find incident 12.
            var digits = term.Split('-', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? term;
            var number = int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;

            query = query.Where(i =>
                i.Id == number ||
                i.Description.ToLower().Contains(term) ||
                (i.Place != null && i.Place.ToLower().Contains(term)) ||
                (i.InvolvedPerson != null && i.InvolvedPerson.ToLower().Contains(term)) ||
                (i.Findings != null && i.Findings.ToLower().Contains(term)) ||
                i.Equipment!.AssetTag.ToLower().Contains(term) ||
                (i.Equipment.Manufacturer != null && i.Equipment.Manufacturer.ToLower().Contains(term)) ||
                (i.Equipment.Model != null && i.Equipment.Model.ToLower().Contains(term)));
        }

        if (type is not null)
        {
            query = query.Where(i => (int)i.Type == type);
        }

        if (equipmentId is not null)
        {
            query = query.Where(i => i.EquipmentId == equipmentId);
        }

        if (mine == true)
        {
            var me = EquipmentMoveEndpoints.UserId(principal);
            query = query.Where(i => i.ReportedByUserId == me);
        }

        if (from is not null)
        {
            query = query.Where(i => i.OccurredOn >= from);
        }

        if (to is not null)
        {
            query = query.Where(i => i.OccurredOn <= to);
        }

        // The tabs' numbers follow the search but not the tab chosen, so each says what it would show.
        var counts = new
        {
            Reported = await query.CountAsync(i => i.Status == IncidentStatus.Reported, ct),
            InReview = await query.CountAsync(i => i.Status == IncidentStatus.InReview, ct),
            Closed = await query.CountAsync(i => i.Status == IncidentStatus.Closed, ct),
        };

        query = StatusFilter(status) switch
        {
            "open" => query.Where(i => i.Status != IncidentStatus.Closed),
            "reported" => query.Where(i => i.Status == IncidentStatus.Reported),
            "review" => query.Where(i => i.Status == IncidentStatus.InReview),
            "closed" => query.Where(i => i.Status == IncidentStatus.Closed),
            _ => query,
        };

        var total = await query.CountAsync(ct);

        var rows = await query
            // What still needs looking at comes first, the oldest first; then the rest, newest first.
            .OrderBy(i => i.Status == IncidentStatus.Closed ? 1 : 0)
            .ThenBy(i => i.Status == IncidentStatus.Closed ? (DateTime?)null : i.ReportedAtUtc)
            .ThenByDescending(i => i.ReportedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new
            {
                i.Id,
                i.ReportedAtUtc,
                i.Type,
                i.OccurredOn,
                i.OccurredAt,
                i.Damage,
                i.Status,
                i.TakenOutOfUse,
                i.Description,
                i.EquipmentId,
                AssetTag = i.Equipment!.AssetTag,
                MachineName = i.Equipment.EquipmentType!.Name,
                LocationName = i.Location == null ? null : i.Location.Name,
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new
        {
            r.Id,
            Reference = Incident.ReferenceFor(r.ReportedAtUtc, r.Id),
            Type = (int)r.Type,
            TypeLabel = IncidentWords.Type(r.Type),
            r.OccurredOn,
            r.OccurredAt,
            Damage = (int)r.Damage,
            DamageLabel = IncidentWords.Damage(r.Damage),
            Status = r.Status.ToString(),
            StatusLabel = IncidentWords.Status(r.Status),
            r.TakenOutOfUse,
            Summary = Shorten(r.Description),
            r.EquipmentId,
            r.AssetTag,
            r.MachineName,
            r.LocationName,
        });

        return Results.Ok(new { items, total, page, pageSize, counts });
    }

    private static async Task<IResult> GetAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        var i = await db.Incidents.AsNoTracking()
            .Include(x => x.Equipment).ThenInclude(m => m!.EquipmentType)
            .Include(x => x.Location)
            .SingleOrDefaultAsync(x => x.Id == id, ct);

        if (i is null)
        {
            return Results.NotFound();
        }

        var names = await NamesAsync(db, [i.ReportedByUserId, i.ClosedByUserId ?? 0], ct);

        return Results.Ok(new
        {
            i.Id,
            i.Reference,
            Type = (int)i.Type,
            TypeLabel = IncidentWords.Type(i.Type),
            i.OccurredOn,
            OccurredAt = i.OccurredAt?.ToString("HH:mm", CultureInfo.InvariantCulture),
            i.Place,
            i.Description,
            i.InvolvedPerson,
            i.ImmediateAction,
            i.TakenOutOfUse,
            Damage = (int)i.Damage,
            DamageLabel = IncidentWords.Damage(i.Damage),
            Status = i.Status.ToString(),
            StatusLabel = IncidentWords.Status(i.Status),
            i.Findings,
            i.CorrectiveAction,
            i.EquipmentId,
            Machine = i.Equipment is null
                ? null
                : new
                {
                    i.Equipment.Id,
                    i.Equipment.AssetTag,
                    MachineName = i.Equipment.EquipmentType?.Name,
                    i.Equipment.Manufacturer,
                    i.Equipment.Model,
                    i.Equipment.SerialNumber,
                },
            LocationName = i.Location?.Name,
            ReportedByName = names.GetValueOrDefault(i.ReportedByUserId),
            i.ReportedAtUtc,
            ClosedByName = i.ClosedByUserId is { } c ? names.GetValueOrDefault(c) : null,
            i.ClosedAtUtc,
        });
    }

    /// <summary>The figures behind the summary: how many, of what kind, where, and which machines come up again and again.</summary>
    private sealed record Summary(
        int Total,
        int Open,
        int Closed,
        int TakenOutOfUse,
        List<Tally> ByType,
        List<Tally> ByDamage,
        List<Tally> ByLocation,
        List<RepeatMachine> RepeatMachines);

    private sealed record Tally(string Label, int Value, int Number);

    private sealed record RepeatMachine(int EquipmentId, string AssetTag, string? MachineName, int Incidents);

    private static async Task<Summary> BuildSummaryAsync(HospitalPmDbContext db, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var query = db.Incidents.AsNoTracking().AsQueryable();
        if (from is not null)
        {
            query = query.Where(i => i.OccurredOn >= from);
        }

        if (to is not null)
        {
            query = query.Where(i => i.OccurredOn <= to);
        }

        var rows = await query
            .Select(i => new
            {
                i.Type,
                i.Damage,
                i.Status,
                i.TakenOutOfUse,
                i.LocationId,
                LocationName = i.Location == null ? null : i.Location.Name,
                i.EquipmentId,
                AssetTag = i.Equipment!.AssetTag,
                MachineName = i.Equipment.EquipmentType!.Name,
            })
            .ToListAsync(ct);

        return new Summary(
            rows.Count,
            rows.Count(r => r.Status != IncidentStatus.Closed),
            rows.Count(r => r.Status == IncidentStatus.Closed),
            rows.Count(r => r.TakenOutOfUse),
            rows.GroupBy(r => r.Type).Select(g => new Tally(IncidentWords.Type(g.Key), g.Count(), (int)g.Key))
                .OrderByDescending(c => c.Value).ThenBy(c => c.Label, StringComparer.Ordinal).ToList(),
            // All four, in order, including those with none: "no machine was beyond repair" is worth saying.
            Enum.GetValues<DamageLevel>().Select(d => new Tally(IncidentWords.Damage(d), rows.Count(r => r.Damage == d), (int)d)).ToList(),
            rows.GroupBy(r => r.LocationName ?? "Not recorded")
                .Select(g => new Tally(g.Key, g.Count(), 0))
                .OrderByDescending(c => c.Value).ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
            rows.GroupBy(r => r.EquipmentId).Where(g => g.Count() >= 2)
                .Select(g => new RepeatMachine(g.Key, g.First().AssetTag, g.First().MachineName, g.Count()))
                .OrderByDescending(m => m.Incidents).ThenBy(m => m.AssetTag, StringComparer.OrdinalIgnoreCase).Take(10).ToList());
    }

    private static async Task<IResult> SummaryAsync(
        HospitalPmDbContext db, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        Results.Ok(await BuildSummaryAsync(db, from, to, ct));

    private static async Task<IResult> SummaryPdfAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        IOptions<ReportOptions> options,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken ct)
    {
        if (from is not null && to is not null && from > to)
        {
            return Results.BadRequest(new { error = "The period ends before it starts." });
        }

        var summary = await BuildSummaryAsync(db, from, to, ct);

        var query = db.Incidents.AsNoTracking().AsQueryable();
        if (from is not null)
        {
            query = query.Where(i => i.OccurredOn >= from);
        }

        if (to is not null)
        {
            query = query.Where(i => i.OccurredOn <= to);
        }

        var rows = await query
            .OrderByDescending(i => i.OccurredOn).ThenByDescending(i => i.Id)
            .Take(500)
            .Select(i => new
            {
                i.Id,
                i.ReportedAtUtc,
                i.OccurredOn,
                i.Type,
                i.Damage,
                i.Status,
                AssetTag = i.Equipment!.AssetTag,
                MachineName = i.Equipment.EquipmentType!.Name,
                LocationName = i.Location == null ? null : i.Location.Name,
            })
            .ToListAsync(ct);

        var data = new IncidentSummaryReportData(
            from,
            to,
            clock.Today(),
            summary.Total,
            summary.Open,
            summary.Closed,
            summary.TakenOutOfUse,
            summary.ByType.Select(c => new IncidentSummaryCount(c.Label, c.Value)).ToList(),
            summary.ByDamage.Select(c => new IncidentSummaryCount(c.Label, c.Value)).ToList(),
            summary.ByLocation.Select(c => new IncidentSummaryCount(c.Label, c.Value)).ToList(),
            summary.RepeatMachines.Select(m => new IncidentSummaryCount($"{m.AssetTag}{(m.MachineName is null ? string.Empty : " - " + m.MachineName)}", m.Incidents)).ToList(),
            rows.Select(r => new IncidentSummaryRow(
                Incident.ReferenceFor(r.ReportedAtUtc, r.Id),
                r.OccurredOn,
                r.AssetTag,
                r.MachineName,
                r.LocationName,
                IncidentWords.Type(r.Type),
                IncidentWords.Damage(r.Damage),
                IncidentWords.Status(r.Status))).ToList(),
            summary.Total > rows.Count);

        var pdf = new IncidentSummaryDocument(data, options.Value).GeneratePdf();

        return Results.File(pdf, "application/pdf", "Incident-report.pdf");
    }

    private static async Task<IResult> PdfAsync(
        int id,
        HospitalPmDbContext db,
        HospitalClock clock,
        IOptions<ReportOptions> options,
        CancellationToken ct)
    {
        var i = await db.Incidents.AsNoTracking()
            .Include(x => x.Equipment).ThenInclude(m => m!.EquipmentType)
            .Include(x => x.Location)
            .SingleOrDefaultAsync(x => x.Id == id, ct);

        if (i is null)
        {
            return Results.NotFound();
        }

        var names = await NamesAsync(db, [i.ReportedByUserId, i.ClosedByUserId ?? 0], ct);

        var data = new IncidentReportData(
            i.Reference,
            IncidentWords.Status(i.Status),
            i.Equipment?.AssetTag,
            i.Equipment?.EquipmentType?.Name,
            i.Equipment?.Manufacturer,
            i.Equipment?.Model,
            i.Equipment?.SerialNumber,
            i.Location?.Name,
            IncidentWords.Type(i.Type),
            i.OccurredOn,
            i.OccurredAt,
            i.Place,
            i.Description,
            i.InvolvedPerson,
            i.ImmediateAction,
            i.TakenOutOfUse,
            IncidentWords.Damage(i.Damage),
            i.Findings,
            i.CorrectiveAction,
            names.GetValueOrDefault(i.ReportedByUserId),
            i.ReportedAtUtc,
            i.ClosedByUserId is { } c ? names.GetValueOrDefault(c) : null,
            i.ClosedAtUtc);

        var pdf = new IncidentReportDocument(data, options.Value, clock.Offset).GeneratePdf();

        return Results.File(pdf, "application/pdf", $"{data.Reference}.pdf");
    }

    // ---------------------------------------------------------------- writing

    private static async Task<IResult> ReportAsync(
        [FromBody] IncidentRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        // Asked of the register as this person sees it: a person from another department can only report on
        // a machine of their own departments, and one outside them is simply "unknown".
        var machine = await db.Equipment.AsNoTracking().SingleOrDefaultAsync(e => e.Id == request.EquipmentId, ct);
        if (machine is null)
        {
            return Results.BadRequest(new { error = "Choose the machine this happened to." });
        }

        var (fields, error) = Validate(request, clock);
        if (error is not null)
        {
            return error;
        }

        var incident = new Incident
        {
            EquipmentId = machine.Id,
            LocationId = machine.LocationId,
            Type = fields!.Type,
            OccurredOn = fields.OccurredOn,
            OccurredAt = fields.OccurredAt,
            Place = fields.Place,
            Description = fields.Description,
            InvolvedPerson = fields.InvolvedPerson,
            ImmediateAction = fields.ImmediateAction,
            TakenOutOfUse = fields.TakenOutOfUse,
            Damage = fields.Damage,
            Status = IncidentStatus.Reported,
            ReportedByUserId = EquipmentMoveEndpoints.UserId(principal),
            ReportedAtUtc = clock.UtcNow(),
            UpdatedAtUtc = clock.UtcNow(),
        };

        db.Incidents.Add(incident);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/incidents/{incident.Id}", new { incident.Id, incident.Reference });
    }

    private static async Task<IResult> UpdateAsync(
        int id,
        [FromBody] IncidentRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        CancellationToken ct)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null)
        {
            return Results.NotFound();
        }

        if (incident.Status == IncidentStatus.Closed)
        {
            return Results.Conflict(new { error = $"{incident.Reference} is closed and can no longer be changed." });
        }

        var (fields, error) = Validate(request, clock);
        if (error is not null)
        {
            return error;
        }

        // The machine is the one it happened to. Writing it up against the wrong one is corrected by choosing another.
        if (request.EquipmentId != incident.EquipmentId)
        {
            var machine = await db.Equipment.AsNoTracking().SingleOrDefaultAsync(e => e.Id == request.EquipmentId, ct);
            if (machine is null)
            {
                return Results.BadRequest(new { error = "Choose the machine this happened to." });
            }

            incident.EquipmentId = machine.Id;
            incident.LocationId = machine.LocationId;
        }

        incident.Type = fields!.Type;
        incident.OccurredOn = fields.OccurredOn;
        incident.OccurredAt = fields.OccurredAt;
        incident.Place = fields.Place;
        incident.Description = fields.Description;
        incident.InvolvedPerson = fields.InvolvedPerson;
        incident.ImmediateAction = fields.ImmediateAction;
        incident.TakenOutOfUse = fields.TakenOutOfUse;
        incident.Damage = fields.Damage;
        incident.UpdatedAtUtc = clock.UtcNow();

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ReviewAsync(
        int id,
        [FromBody] IncidentReviewRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        CancellationToken ct)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null)
        {
            return Results.NotFound();
        }

        if (incident.Status == IncidentStatus.Closed)
        {
            return Results.Conflict(new { error = $"{incident.Reference} is closed and can no longer be changed." });
        }

        if (ApplyReview(incident, request) is { } problem)
        {
            return problem;
        }

        // Looking into it, even to write nothing yet, is what moves it on from "reported".
        incident.Status = IncidentStatus.InReview;
        incident.UpdatedAtUtc = clock.UtcNow();

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> CloseAsync(
        int id,
        [FromBody] IncidentReviewRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var incident = await db.Incidents.SingleOrDefaultAsync(i => i.Id == id, ct);
        if (incident is null)
        {
            return Results.NotFound();
        }

        if (incident.Status == IncidentStatus.Closed)
        {
            return Results.Conflict(new { error = $"{incident.Reference} is already closed." });
        }

        if (ApplyReview(incident, request) is { } problem)
        {
            return problem;
        }

        if (string.IsNullOrWhiteSpace(incident.Findings))
        {
            return Results.BadRequest(new { error = "Say what caused it before closing the incident." });
        }

        var now = clock.UtcNow();
        incident.Status = IncidentStatus.Closed;
        incident.ClosedAtUtc = now;
        incident.ClosedByUserId = EquipmentMoveEndpoints.UserId(principal);
        incident.UpdatedAtUtc = now;

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    // ---------------------------------------------------------------- rules

    private sealed record Fields(
        IncidentType Type,
        DateOnly OccurredOn,
        TimeOnly? OccurredAt,
        string? Place,
        string Description,
        string? InvolvedPerson,
        string? ImmediateAction,
        bool TakenOutOfUse,
        DamageLevel Damage);

    private static (Fields? Fields, IResult? Error) Validate(IncidentRequest request, HospitalClock clock)
    {
        static (Fields?, IResult?) Bad(string message) => (null, Results.BadRequest(new { error = message }));

        if (!Enum.IsDefined((IncidentType)request.Type))
        {
            return Bad("Choose what happened.");
        }

        var damage = request.Damage ?? (int)DamageLevel.None;
        if (!Enum.IsDefined((DamageLevel)damage))
        {
            return Bad("Choose the state the machine was left in.");
        }

        var description = request.Description?.Trim() ?? string.Empty;
        if (description.Length == 0)
        {
            return Bad("Describe what happened to the machine.");
        }

        if (description.Length > MaxLongTextLength || request.ImmediateAction?.Trim().Length > MaxLongTextLength)
        {
            return Bad($"What happened, and what was done, can each be at most {MaxLongTextLength} characters.");
        }

        if (request.Place?.Trim().Length > MaxTextLength || request.InvolvedPerson?.Trim().Length > MaxTextLength)
        {
            return Bad($"Where it happened and who was involved can each be at most {MaxTextLength} characters.");
        }

        var today = clock.Today();
        var occurredOn = request.OccurredOn ?? today;
        if (occurredOn.Year < 2000 || occurredOn > today)
        {
            return Bad("Give the day it happened. It cannot be a day that has not come yet.");
        }

        TimeOnly? at = null;
        if (!string.IsNullOrWhiteSpace(request.OccurredAt))
        {
            if (!TimeOnly.TryParseExact(request.OccurredAt.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return Bad("Give the time as hours and minutes, like 14:30, or leave it out.");
            }

            at = parsed;
        }

        return (new Fields(
            (IncidentType)request.Type,
            occurredOn,
            at,
            Blank(request.Place),
            description,
            Blank(request.InvolvedPerson),
            Blank(request.ImmediateAction),
            request.TakenOutOfUse ?? false,
            (DamageLevel)damage), null);
    }

    /// <summary>Writes what the review says onto the incident. Null when it is fine, otherwise the refusal.</summary>
    private static IResult? ApplyReview(Incident incident, IncidentReviewRequest request)
    {
        if (request.Findings?.Length > MaxLongTextLength || request.CorrectiveAction?.Length > MaxLongTextLength)
        {
            return Results.BadRequest(new { error = $"What was found, and what is being done, can each be at most {MaxLongTextLength} characters." });
        }

        if (request.Damage is { } damage)
        {
            if (!Enum.IsDefined((DamageLevel)damage))
            {
                return Results.BadRequest(new { error = "Choose the state the machine was left in." });
            }

            incident.Damage = (DamageLevel)damage;
        }

        // What is sent replaces what was there, including a blank, so a finding can be taken out as well as put in.
        if (request.Findings is not null)
        {
            incident.Findings = Blank(request.Findings);
        }

        if (request.CorrectiveAction is not null)
        {
            incident.CorrectiveAction = Blank(request.CorrectiveAction);
        }

        return null;
    }

    // ---------------------------------------------------------------- small things

    private static string? StatusFilter(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" => string.Empty,
        "open" => "open",
        "reported" => "reported",
        "review" => "review",
        "closed" => "closed",
        _ => null,
    };

    private static async Task<Dictionary<int, string>> NamesAsync(HospitalPmDbContext db, IEnumerable<int> ids, CancellationToken ct)
    {
        var wanted = ids.Where(i => i > 0).Distinct().ToList();
        return await db.Users.AsNoTracking()
            .Where(u => wanted.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    /// <summary>The first line or so of what happened, for a list.</summary>
    private static string Shorten(string text)
    {
        var line = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return line.Length <= 140 ? line : line[..140].TrimEnd() + "…";
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
