using System.Globalization;
using System.Security.Claims;
using HospitalPm.Api.Auth;
using HospitalPm.Api.Equipment;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Losses;
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

namespace HospitalPm.Api.Losses;

public sealed record LossRequest(
    int EquipmentId,
    int Kind,
    // Left out, it was found missing today.
    DateOnly? NoticedOn,
    DateOnly? LastSeenOn,
    string? LastSeenPlace,
    string? Description,
    string? LastHandledBy,
    string? PoliceReportNumber,
    DateOnly? PoliceReportedOn,
    // Left out, the machine's cost on the register.
    decimal? EstimatedValue);

/// <summary>What the biomedical team writes while looking into it. Fields left out are not changed; a blank text takes one out.</summary>
public sealed record LossReviewRequest(
    string? Findings,
    string? CorrectiveAction,
    string? PoliceReportNumber,
    DateOnly? PoliceReportedOn,
    decimal? EstimatedValue);

/// <summary>
/// How a loss ends: the day it was found (recovered), what was found out and is being done, and the decision maker's own
/// words. For a write-off the head's final decision is what counts; the rest is read from the record.
/// </summary>
public sealed record LossCloseRequest(DateOnly? RecoveredOn, string? Findings, string? CorrectiveAction, string? DecisionNotes = null);

/// <summary>Why the head sends a loss back to the engineer. Needed: the engineer has to know what is wanted.</summary>
public sealed record LossSendBackRequest(string? Reason);

/// <summary>
/// Loss of equipment: a machine that was misplaced, stolen, or lost while it was away.
///
/// It goes up in three steps. The DEPARTMENT writes it up; a biomedical ENGINEER looks into it and approves it (or rejects
/// it as not lost); the HEAD of Biomedical makes the final decision: write it off, or send it back to the engineer. An
/// engineer or the head can write one up too, and then it starts at the head's desk, because the engineer's step is
/// theirs already. At any point before it ends the machine can turn up, and an engineer or the head marks it found.
///
/// While it is open the register shows the machine as Lost, so it drops out of maintenance and cannot be sent out; a found
/// machine goes back to the status it had. Only the department, the engineers and the head see any of it (the IT team does
/// not). A person from another department writes up and reads the losses of their own departments, and nothing outside
/// them (see HospitalPmDbContext.RestrictTo); the biomedical engineers and the head see all of them.
///
/// Nothing here is about a patient. It is about the machine. See CLAUDE.md.
/// </summary>
public static class LossEndpoints
{
    /// <summary>The statuses that are the end of a loss, as a list a database query can use.</summary>
    private static class LossWordsStatuses
    {
        public static readonly LossStatus[] Final = [LossStatus.Recovered, LossStatus.WrittenOff, LossStatus.Cancelled];
    }

    private const int MaxPageSize = 200;
    private const int MaxTextLength = 200;
    private const int MaxLongTextLength = 4000;
    private const decimal MaxValue = 100_000_000_000m;

    public static void MapLossEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/losses").WithTags("Loss of equipment").RequirePermission(Permissions.LossesView);

        group.MapGet("/", ListAsync);
        group.MapGet("/summary", SummaryAsync);
        group.MapGet("/report.pdf", SummaryPdfAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapGet("/{id:int}/report.pdf", PdfAsync);

        // Step 1: the department (or an engineer, or the head) writes it up.
        group.MapPost("/", ReportAsync).RequirePermission(Permissions.LossesReport);

        // Step 2: a biomedical engineer looks into it, and approves it for the head, or rejects it as not lost.
        group.MapPut("/{id:int}", UpdateAsync).RequirePermission(Permissions.LossesReview);
        group.MapPut("/{id:int}/review", ReviewAsync).RequirePermission(Permissions.LossesReview);
        group.MapPost("/{id:int}/approve", ApproveAsync).RequirePermission(Permissions.LossesReview);
        group.MapPost("/{id:int}/recover", RecoverAsync).RequirePermission(Permissions.LossesReview);
        group.MapPost("/{id:int}/cancel", CancelAsync).RequirePermission(Permissions.LossesReview);

        // Step 3: the head's final decision.
        group.MapPost("/{id:int}/write-off", WriteOffAsync).RequirePermission(Permissions.LossesDecide);
        group.MapPost("/{id:int}/send-back", SendBackAsync).RequirePermission(Permissions.LossesDecide);
    }

    // ---------------------------------------------------------------- reading

    private static async Task<IResult> ListAsync(
        HospitalPmDbContext db,
        ClaimsPrincipal principal,
        [FromQuery] string? q,
        [FromQuery] string? status,
        [FromQuery] int? kind,
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
            return Results.BadRequest(new { error = "The status must be open, engineer, reported, investigating, awaiting, recovered, written-off, cancelled or all." });
        }

        if (kind is { } k && !Enum.IsDefined((LossKind)k))
        {
            return Results.BadRequest(new { error = "Unknown kind of loss." });
        }

        var query = db.EquipmentLosses.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            // "LOSS-2026-00012", "loss-2026-12" and "12" all find loss 12.
            var digits = term.Split('-', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? term;
            var number = int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;

            query = query.Where(l =>
                l.Id == number ||
                l.Description.ToLower().Contains(term) ||
                (l.LastSeenPlace != null && l.LastSeenPlace.ToLower().Contains(term)) ||
                (l.LastHandledBy != null && l.LastHandledBy.ToLower().Contains(term)) ||
                (l.PoliceReportNumber != null && l.PoliceReportNumber.ToLower().Contains(term)) ||
                (l.Findings != null && l.Findings.ToLower().Contains(term)) ||
                l.Equipment!.AssetTag.ToLower().Contains(term) ||
                (l.Equipment.Manufacturer != null && l.Equipment.Manufacturer.ToLower().Contains(term)) ||
                (l.Equipment.Model != null && l.Equipment.Model.ToLower().Contains(term)));
        }

        if (kind is not null)
        {
            query = query.Where(l => (int)l.Kind == kind);
        }

        if (equipmentId is not null)
        {
            query = query.Where(l => l.EquipmentId == equipmentId);
        }

        if (mine == true)
        {
            var me = EquipmentMoveEndpoints.UserId(principal);
            query = query.Where(l => l.ReportedByUserId == me);
        }

        if (from is not null)
        {
            query = query.Where(l => l.NoticedOn >= from);
        }

        if (to is not null)
        {
            query = query.Where(l => l.NoticedOn <= to);
        }

        // The tabs' numbers follow the search but not the tab chosen, so each says what it would show.
        var counts = new
        {
            // Everything still being dealt with, and the two desks it can be waiting at.
            Open = await query.CountAsync(l => l.Status == LossStatus.Reported || l.Status == LossStatus.Investigating || l.Status == LossStatus.AwaitingDecision, ct),
            WithEngineer = await query.CountAsync(l => l.Status == LossStatus.Reported || l.Status == LossStatus.Investigating, ct),
            AwaitingDecision = await query.CountAsync(l => l.Status == LossStatus.AwaitingDecision, ct),
            Recovered = await query.CountAsync(l => l.Status == LossStatus.Recovered, ct),
            WrittenOff = await query.CountAsync(l => l.Status == LossStatus.WrittenOff, ct),
            Cancelled = await query.CountAsync(l => l.Status == LossStatus.Cancelled, ct),
        };

        query = StatusFilter(status) switch
        {
            "open" => query.Where(l => l.Status == LossStatus.Reported || l.Status == LossStatus.Investigating || l.Status == LossStatus.AwaitingDecision),
            "engineer" => query.Where(l => l.Status == LossStatus.Reported || l.Status == LossStatus.Investigating),
            "reported" => query.Where(l => l.Status == LossStatus.Reported),
            "investigating" => query.Where(l => l.Status == LossStatus.Investigating),
            "awaiting" => query.Where(l => l.Status == LossStatus.AwaitingDecision),
            "recovered" => query.Where(l => l.Status == LossStatus.Recovered),
            "written-off" => query.Where(l => l.Status == LossStatus.WrittenOff),
            "cancelled" => query.Where(l => l.Status == LossStatus.Cancelled),
            _ => query,
        };

        var total = await query.CountAsync(ct);

        var rows = await query
            // What is still missing comes first, the one gone longest at the top; then the rest, newest first.
            .OrderBy(l => l.Status == LossStatus.Recovered || l.Status == LossStatus.WrittenOff || l.Status == LossStatus.Cancelled ? 1 : 0)
            .ThenBy(l => l.Status == LossStatus.Recovered || l.Status == LossStatus.WrittenOff || l.Status == LossStatus.Cancelled ? (DateTime?)null : l.ReportedAtUtc)
            .ThenByDescending(l => l.ReportedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new
            {
                l.Id,
                l.ReportedAtUtc,
                l.Kind,
                l.NoticedOn,
                l.Status,
                l.EstimatedValue,
                l.PoliceReportNumber,
                l.Description,
                l.EquipmentId,
                AssetTag = l.Equipment!.AssetTag,
                MachineName = l.Equipment.EquipmentType!.Name,
                LocationName = l.Location == null ? null : l.Location.Name,
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new
        {
            r.Id,
            Reference = EquipmentLoss.ReferenceFor(r.ReportedAtUtc, r.Id),
            Kind = (int)r.Kind,
            KindLabel = LossWords.Kind(r.Kind),
            r.NoticedOn,
            Status = r.Status.ToString(),
            StatusLabel = LossWords.Status(r.Status),
            r.EstimatedValue,
            HasPoliceReport = !string.IsNullOrWhiteSpace(r.PoliceReportNumber),
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
        var l = await db.EquipmentLosses.AsNoTracking()
            .Include(x => x.Equipment).ThenInclude(m => m!.EquipmentType)
            .Include(x => x.Location)
            .SingleOrDefaultAsync(x => x.Id == id, ct);

        if (l is null)
        {
            return Results.NotFound();
        }

        var names = await NamesAsync(db, [l.ReportedByUserId, l.ClosedByUserId ?? 0, l.ApprovedByUserId ?? 0], ct);

        return Results.Ok(new
        {
            l.Id,
            l.Reference,
            Kind = (int)l.Kind,
            KindLabel = LossWords.Kind(l.Kind),
            l.NoticedOn,
            l.LastSeenOn,
            l.LastSeenPlace,
            l.Description,
            l.LastHandledBy,
            l.PoliceReportNumber,
            l.PoliceReportedOn,
            l.EstimatedValue,
            Status = l.Status.ToString(),
            StatusLabel = LossWords.Status(l.Status),
            IsFinal = LossWords.IsFinal(l.Status),
            l.Findings,
            l.CorrectiveAction,
            l.DecisionNotes,
            ApprovedByName = l.ApprovedByUserId is { } a ? names.GetValueOrDefault(a) : null,
            l.ApprovedAtUtc,
            l.RecoveredOn,
            l.EquipmentId,
            Machine = l.Equipment is null
                ? null
                : new
                {
                    l.Equipment.Id,
                    l.Equipment.AssetTag,
                    MachineName = l.Equipment.EquipmentType?.Name,
                    l.Equipment.Manufacturer,
                    l.Equipment.Model,
                    l.Equipment.SerialNumber,
                    l.Equipment.PurchaseCost,
                },
            LocationName = l.Location?.Name,
            ReportedByName = names.GetValueOrDefault(l.ReportedByUserId),
            l.ReportedAtUtc,
            ClosedByName = l.ClosedByUserId is { } c ? names.GetValueOrDefault(c) : null,
            l.ClosedAtUtc,
        });
    }

    /// <summary>The figures behind the summary: how many, how they went, what they were worth, and which machines go missing again and again.</summary>
    private sealed record Summary(
        int Total,
        int Open,
        int Recovered,
        int WrittenOff,
        decimal ValueReported,
        decimal ValueLost,
        decimal ValueRecovered,
        int WithoutValue,
        List<Tally> ByKind,
        List<Tally> ByLocation,
        List<RepeatMachine> RepeatMachines);

    private sealed record Tally(string Label, int Value, int Number);

    private sealed record RepeatMachine(int EquipmentId, string AssetTag, string? MachineName, int Losses);

    private static IQueryable<EquipmentLoss> InPeriod(HospitalPmDbContext db, DateOnly? from, DateOnly? to)
    {
        // A loss written up by mistake is not a loss: it is left out of every figure.
        var query = db.EquipmentLosses.AsNoTracking().Where(l => l.Status != LossStatus.Cancelled);
        if (from is not null)
        {
            query = query.Where(l => l.NoticedOn >= from);
        }

        if (to is not null)
        {
            query = query.Where(l => l.NoticedOn <= to);
        }

        return query;
    }

    private static async Task<Summary> BuildSummaryAsync(HospitalPmDbContext db, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var rows = await InPeriod(db, from, to)
            .Select(l => new
            {
                l.Kind,
                l.Status,
                l.EstimatedValue,
                l.LocationId,
                LocationName = l.Location == null ? null : l.Location.Name,
                l.EquipmentId,
                AssetTag = l.Equipment!.AssetTag,
                MachineName = l.Equipment.EquipmentType!.Name,
            })
            .ToListAsync(ct);

        var recovered = rows.Where(r => r.Status == LossStatus.Recovered).ToList();
        var notRecovered = rows.Where(r => r.Status != LossStatus.Recovered).ToList();

        return new Summary(
            rows.Count,
            rows.Count(r => LossWords.IsOpen(r.Status)),
            recovered.Count,
            rows.Count(r => r.Status == LossStatus.WrittenOff),
            rows.Sum(r => r.EstimatedValue ?? 0m),
            notRecovered.Sum(r => r.EstimatedValue ?? 0m),
            recovered.Sum(r => r.EstimatedValue ?? 0m),
            rows.Count(r => r.EstimatedValue is null),
            // All three kinds, in order, including those with none: "nothing was stolen" is worth saying.
            Enum.GetValues<LossKind>().Select(k => new Tally(LossWords.Kind(k), rows.Count(r => r.Kind == k), (int)k)).ToList(),
            rows.GroupBy(r => r.LocationName ?? "Not recorded")
                .Select(g => new Tally(g.Key, g.Count(), 0))
                .OrderByDescending(c => c.Value).ThenBy(c => c.Label, StringComparer.OrdinalIgnoreCase).Take(10).ToList(),
            rows.GroupBy(r => r.EquipmentId).Where(g => g.Count() >= 2)
                .Select(g => new RepeatMachine(g.Key, g.First().AssetTag, g.First().MachineName, g.Count()))
                .OrderByDescending(m => m.Losses).ThenBy(m => m.AssetTag, StringComparer.OrdinalIgnoreCase).Take(10).ToList());
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

        var rows = await InPeriod(db, from, to)
            .OrderByDescending(l => l.NoticedOn).ThenByDescending(l => l.Id)
            .Take(500)
            .Select(l => new
            {
                l.Id,
                l.ReportedAtUtc,
                l.NoticedOn,
                l.Kind,
                l.Status,
                l.EstimatedValue,
                AssetTag = l.Equipment!.AssetTag,
                MachineName = l.Equipment.EquipmentType!.Name,
                LocationName = l.Location == null ? null : l.Location.Name,
            })
            .ToListAsync(ct);

        var data = new LossSummaryReportData(
            from,
            to,
            clock.Today(),
            summary.Total,
            summary.Open,
            summary.Recovered,
            summary.WrittenOff,
            summary.ValueReported,
            summary.ValueLost,
            summary.ValueRecovered,
            summary.WithoutValue,
            summary.ByKind.Select(c => new LossSummaryCount(c.Label, c.Value)).ToList(),
            summary.ByLocation.Select(c => new LossSummaryCount(c.Label, c.Value)).ToList(),
            summary.RepeatMachines.Select(m => new LossSummaryCount($"{m.AssetTag}{(m.MachineName is null ? string.Empty : " - " + m.MachineName)}", m.Losses)).ToList(),
            rows.Select(r => new LossSummaryRow(
                EquipmentLoss.ReferenceFor(r.ReportedAtUtc, r.Id),
                r.NoticedOn,
                r.AssetTag,
                r.MachineName,
                r.LocationName,
                LossWords.Kind(r.Kind),
                r.EstimatedValue,
                LossWords.Status(r.Status))).ToList(),
            summary.Total > rows.Count);

        var pdf = new LossSummaryDocument(data, options.Value).GeneratePdf();

        return Results.File(pdf, "application/pdf", "Loss-of-equipment-report.pdf");
    }

    private static async Task<IResult> PdfAsync(
        int id,
        HospitalPmDbContext db,
        HospitalClock clock,
        IOptions<ReportOptions> options,
        CancellationToken ct)
    {
        var l = await db.EquipmentLosses.AsNoTracking()
            .Include(x => x.Equipment).ThenInclude(m => m!.EquipmentType)
            .Include(x => x.Location)
            .SingleOrDefaultAsync(x => x.Id == id, ct);

        if (l is null)
        {
            return Results.NotFound();
        }

        var names = await NamesAsync(db, [l.ReportedByUserId, l.ClosedByUserId ?? 0, l.ApprovedByUserId ?? 0], ct);

        var data = new LossReportData(
            l.Reference,
            LossWords.Status(l.Status),
            l.Equipment?.AssetTag,
            l.Equipment?.EquipmentType?.Name,
            l.Equipment?.Manufacturer,
            l.Equipment?.Model,
            l.Equipment?.SerialNumber,
            l.Location?.Name,
            LossWords.Kind(l.Kind),
            l.NoticedOn,
            l.LastSeenOn,
            l.LastSeenPlace,
            l.Description,
            l.LastHandledBy,
            l.PoliceReportNumber,
            l.PoliceReportedOn,
            l.EstimatedValue,
            l.Findings,
            l.CorrectiveAction,
            l.RecoveredOn,
            names.GetValueOrDefault(l.ReportedByUserId),
            l.ReportedAtUtc,
            l.ClosedByUserId is { } c ? names.GetValueOrDefault(c) : null,
            l.ClosedAtUtc,
            l.ApprovedByUserId is { } a ? names.GetValueOrDefault(a) : null,
            l.ApprovedAtUtc,
            l.DecisionNotes);

        var pdf = new LossReportDocument(data, options.Value, clock.Offset).GeneratePdf();

        return Results.File(pdf, "application/pdf", $"{data.Reference}.pdf");
    }

    // ---------------------------------------------------------------- writing

    private static async Task<IResult> ReportAsync(
        [FromBody] LossRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal principal,
        PermissionService permissions,
        CancellationToken ct)
    {
        // Asked of the register as this person sees it: a person from another department can only report on
        // a machine of their own departments, and one outside them is simply "unknown".
        var machine = await db.Equipment.SingleOrDefaultAsync(e => e.Id == request.EquipmentId, ct);
        if (machine is null)
        {
            return Results.BadRequest(new { error = "Choose the machine that was lost." });
        }

        if (machine.Status == EquipmentStatus.Disposed)
        {
            return Results.Conflict(new { error = $"{machine.AssetTag} has already been disposed of, so it cannot be recorded as lost." });
        }

        if (machine.Status == EquipmentStatus.Lost
            || await db.EquipmentLosses.AnyAsync(l => l.EquipmentId == machine.Id && !LossWordsStatuses.Final.Contains(l.Status), ct))
        {
            return Results.Conflict(new { error = $"{machine.AssetTag} is already recorded as lost. Add to that record instead." });
        }

        var (fields, error) = Validate(request, clock);
        if (error is not null)
        {
            return error;
        }

        var now = clock.UtcNow();
        var me = EquipmentMoveEndpoints.UserId(principal);

        // A department's loss waits for an engineer. One written up by an engineer or the head has had the engineer's step
        // already (they are the engineer), so it goes straight to the head, with them as the one who approved it.
        var engineersOwn = await permissions.CanAsync(principal, Permissions.LossesReview, ct);

        var loss = new EquipmentLoss
        {
            EquipmentId = machine.Id,
            LocationId = machine.LocationId,
            Kind = fields!.Kind,
            NoticedOn = fields.NoticedOn,
            LastSeenOn = fields.LastSeenOn,
            LastSeenPlace = fields.LastSeenPlace,
            Description = fields.Description,
            LastHandledBy = fields.LastHandledBy,
            PoliceReportNumber = fields.PoliceReportNumber,
            PoliceReportedOn = fields.PoliceReportedOn,
            // What the reporter said, or what the register says the machine cost.
            EstimatedValue = fields.EstimatedValue ?? machine.PurchaseCost,
            Status = engineersOwn ? LossStatus.AwaitingDecision : LossStatus.Reported,
            ApprovedByUserId = engineersOwn ? me : null,
            ApprovedAtUtc = engineersOwn ? now : null,
            EquipmentStatusBefore = machine.Status,
            ReportedByUserId = me,
            ReportedAtUtc = now,
            UpdatedAtUtc = now,
        };

        // Lost is lost: out of maintenance, and not to be sent out, until the record says it is found.
        machine.Status = EquipmentStatus.Lost;

        db.EquipmentLosses.Add(loss);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/losses/{loss.Id}", new { loss.Id, loss.Reference, Status = loss.Status.ToString() });
    }

    private static async Task<IResult> UpdateAsync(
        int id,
        [FromBody] LossRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        CancellationToken ct)
    {
        var loss = await db.EquipmentLosses.SingleOrDefaultAsync(l => l.Id == id, ct);
        if (loss is null)
        {
            return Results.NotFound();
        }

        if (LossWords.IsFinal(loss.Status))
        {
            return Results.Conflict(new { error = $"{loss.Reference} is closed and can no longer be changed." });
        }

        // The machine is the one that was lost. Writing it up against the wrong one is put right by cancelling and writing a new one.
        if (request.EquipmentId != loss.EquipmentId)
        {
            return Results.BadRequest(new { error = "The machine cannot be changed. Cancel this record and write a new one for the right machine." });
        }

        var (fields, error) = Validate(request, clock);
        if (error is not null)
        {
            return error;
        }

        loss.Kind = fields!.Kind;
        loss.NoticedOn = fields.NoticedOn;
        loss.LastSeenOn = fields.LastSeenOn;
        loss.LastSeenPlace = fields.LastSeenPlace;
        loss.Description = fields.Description;
        loss.LastHandledBy = fields.LastHandledBy;
        loss.PoliceReportNumber = fields.PoliceReportNumber;
        loss.PoliceReportedOn = fields.PoliceReportedOn;
        loss.EstimatedValue = fields.EstimatedValue ?? loss.EstimatedValue;
        loss.UpdatedAtUtc = clock.UtcNow();

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> ReviewAsync(
        int id,
        [FromBody] LossReviewRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        CancellationToken ct)
    {
        var loss = await db.EquipmentLosses.SingleOrDefaultAsync(l => l.Id == id, ct);
        if (loss is null)
        {
            return Results.NotFound();
        }

        if (LossWords.IsFinal(loss.Status))
        {
            return Results.Conflict(new { error = $"{loss.Reference} is closed and can no longer be changed." });
        }

        if (ApplyReview(loss, request) is { } problem)
        {
            return problem;
        }

        // Looking into it, even to write nothing yet, is what moves it on from "reported". One already waiting for the head
        // stays there: saving a note does not take it back.
        if (loss.Status == LossStatus.Reported)
        {
            loss.Status = LossStatus.Investigating;
        }

        loss.UpdatedAtUtc = clock.UtcNow();

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<IResult> RecoverAsync(
        int id,
        [FromBody] LossCloseRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var loss = await db.EquipmentLosses.SingleOrDefaultAsync(l => l.Id == id, ct);
        if (loss is null)
        {
            return Results.NotFound();
        }

        if (LossWords.IsFinal(loss.Status))
        {
            return Results.Conflict(new { error = $"{loss.Reference} is already closed." });
        }

        var today = clock.Today();
        var day = request.RecoveredOn ?? today;
        if (day > today)
        {
            return Results.BadRequest(new { error = "The day it was found cannot be a day that has not happened yet." });
        }

        if (day < loss.NoticedOn)
        {
            return Results.BadRequest(new { error = $"It cannot have been found before it was missed, on {loss.NoticedOn:dd/MM/yyyy}." });
        }

        if (ApplyFindings(loss, request) is { } problem)
        {
            return problem;
        }

        loss.Status = LossStatus.Recovered;
        loss.RecoveredOn = day;
        loss.DecisionNotes = Blank(request.DecisionNotes) ?? loss.DecisionNotes;
        await CloseAsync(db, loss, clock, principal, putBack: true, ct);

        return Results.NoContent();
    }

    /// <summary>Step 2, done: an engineer says what they found out and sends it to the head for the final decision.</summary>
    private static async Task<IResult> ApproveAsync(
        int id,
        [FromBody] LossReviewRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var loss = await db.EquipmentLosses.SingleOrDefaultAsync(l => l.Id == id, ct);
        if (loss is null)
        {
            return Results.NotFound();
        }

        if (loss.Status == LossStatus.AwaitingDecision)
        {
            return Results.Conflict(new { error = $"{loss.Reference} has already been approved and is waiting for the head's decision." });
        }

        if (LossWords.IsFinal(loss.Status))
        {
            return Results.Conflict(new { error = $"{loss.Reference} is already closed." });
        }

        if (ApplyReview(loss, request) is { } problem)
        {
            return problem;
        }

        if (string.IsNullOrWhiteSpace(loss.Findings))
        {
            return Results.BadRequest(new { error = "Say what you found out about how it was lost before sending it to the head." });
        }

        var now = clock.UtcNow();
        loss.Status = LossStatus.AwaitingDecision;
        loss.ApprovedByUserId = EquipmentMoveEndpoints.UserId(principal);
        loss.ApprovedAtUtc = now;
        // Whatever the head said when it was last sent back has been answered.
        loss.DecisionNotes = null;
        loss.UpdatedAtUtc = now;

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    /// <summary>Step 3, not yet: the head wants more done, so it goes back to the engineer with what is wanted.</summary>
    private static async Task<IResult> SendBackAsync(
        int id,
        [FromBody] LossSendBackRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        CancellationToken ct)
    {
        var loss = await db.EquipmentLosses.SingleOrDefaultAsync(l => l.Id == id, ct);
        if (loss is null)
        {
            return Results.NotFound();
        }

        if (loss.Status != LossStatus.AwaitingDecision)
        {
            return Results.Conflict(new { error = LossWords.IsFinal(loss.Status)
                ? $"{loss.Reference} is already closed."
                : $"{loss.Reference} is not waiting for the head's decision, so there is nothing to send back." });
        }

        var reason = Blank(request.Reason);
        if (reason is null)
        {
            return Results.BadRequest(new { error = "Say what the engineer is to do before sending it back." });
        }

        if (reason.Length > MaxLongTextLength)
        {
            return Results.BadRequest(new { error = $"The reason can be at most {MaxLongTextLength} characters." });
        }

        loss.Status = LossStatus.Investigating;
        loss.DecisionNotes = reason;
        // It has to be approved again once it has been looked into again.
        loss.ApprovedByUserId = null;
        loss.ApprovedAtUtc = null;
        loss.UpdatedAtUtc = clock.UtcNow();

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    /// <summary>Step 3, done: the head accepts that the machine is not coming back. Only from a loss an engineer has approved.</summary>
    private static async Task<IResult> WriteOffAsync(
        int id,
        [FromBody] LossCloseRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var loss = await db.EquipmentLosses.SingleOrDefaultAsync(l => l.Id == id, ct);
        if (loss is null)
        {
            return Results.NotFound();
        }

        if (LossWords.IsFinal(loss.Status))
        {
            return Results.Conflict(new { error = $"{loss.Reference} is already closed." });
        }

        // The head decides what an engineer has put in front of them. A loss nobody has looked into is not theirs to write off yet.
        if (loss.Status != LossStatus.AwaitingDecision)
        {
            return Results.Conflict(new { error = $"{loss.Reference} has not been approved by a biomedical engineer yet, so it is not ready for the head's decision." });
        }

        if (ApplyFindings(loss, request) is { } problem)
        {
            return problem;
        }

        if (string.IsNullOrWhiteSpace(loss.Findings))
        {
            return Results.BadRequest(new { error = "Say what was found out about how it was lost before writing it off." });
        }

        // The machine stays on the register as Lost: it is not coming back, and its history is kept.
        loss.Status = LossStatus.WrittenOff;
        loss.DecisionNotes = Blank(request.DecisionNotes) ?? loss.DecisionNotes;
        await CloseAsync(db, loss, clock, principal, putBack: false, ct);

        return Results.NoContent();
    }

    private static async Task<IResult> CancelAsync(
        int id,
        [FromBody] LossCloseRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var loss = await db.EquipmentLosses.SingleOrDefaultAsync(l => l.Id == id, ct);
        if (loss is null)
        {
            return Results.NotFound();
        }

        if (LossWords.IsFinal(loss.Status))
        {
            return Results.Conflict(new { error = $"{loss.Reference} is already closed." });
        }

        if (request.Findings?.Length > MaxLongTextLength)
        {
            return Results.BadRequest(new { error = $"The reason can be at most {MaxLongTextLength} characters." });
        }

        if (string.IsNullOrWhiteSpace(request.Findings))
        {
            return Results.BadRequest(new { error = "Say why it is being cancelled, for example that the machine was not lost." });
        }

        loss.Findings = Blank(request.Findings);
        loss.Status = LossStatus.Cancelled;
        // It was never lost, so the machine is put back as it was.
        await CloseAsync(db, loss, clock, principal, putBack: true, ct);

        return Results.NoContent();
    }

    /// <summary>Ends a loss: who and when, and the machine put back on the register when it is not lost after all.</summary>
    private static async Task CloseAsync(
        HospitalPmDbContext db, EquipmentLoss loss, HospitalClock clock, ClaimsPrincipal principal, bool putBack, CancellationToken ct)
    {
        var now = clock.UtcNow();
        loss.ClosedAtUtc = now;
        loss.ClosedByUserId = EquipmentMoveEndpoints.UserId(principal);
        loss.UpdatedAtUtc = now;

        if (putBack)
        {
            // Only a machine that is still Lost is touched: if someone has since set its status another way, that is a
            // decision this does not undo.
            var machine = await db.Equipment.SingleOrDefaultAsync(e => e.Id == loss.EquipmentId, ct);
            if (machine is { Status: EquipmentStatus.Lost })
            {
                machine.Status = loss.EquipmentStatusBefore is { } before && before != EquipmentStatus.Lost
                    ? before
                    : EquipmentStatus.InService;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- rules

    private sealed record Fields(
        LossKind Kind,
        DateOnly NoticedOn,
        DateOnly? LastSeenOn,
        string? LastSeenPlace,
        string Description,
        string? LastHandledBy,
        string? PoliceReportNumber,
        DateOnly? PoliceReportedOn,
        decimal? EstimatedValue);

    private static (Fields? Fields, IResult? Error) Validate(LossRequest request, HospitalClock clock)
    {
        static (Fields?, IResult?) Bad(string message) => (null, Results.BadRequest(new { error = message }));

        if (!Enum.IsDefined((LossKind)request.Kind))
        {
            return Bad("Choose how the machine was lost.");
        }

        var description = request.Description?.Trim() ?? string.Empty;
        if (description.Length == 0)
        {
            return Bad("Describe how the machine was lost.");
        }

        if (description.Length > MaxLongTextLength)
        {
            return Bad($"What happened can be at most {MaxLongTextLength} characters.");
        }

        if (request.LastSeenPlace?.Trim().Length > MaxTextLength || request.LastHandledBy?.Trim().Length > MaxTextLength)
        {
            return Bad($"Where it was last seen and who last had it can each be at most {MaxTextLength} characters.");
        }

        if (request.PoliceReportNumber?.Trim().Length > 100)
        {
            return Bad("The police complaint number can be at most 100 characters.");
        }

        var today = clock.Today();
        var noticed = request.NoticedOn ?? today;
        if (noticed.Year < 2000 || noticed > today)
        {
            return Bad("Give the day the machine was found to be missing. It cannot be a day that has not come yet.");
        }

        if (request.LastSeenOn is { } seen && (seen.Year < 2000 || seen > noticed))
        {
            return Bad("It cannot have been last seen after the day it was found to be missing.");
        }

        if (request.PoliceReportedOn is { } told && (told.Year < 2000 || told > today))
        {
            return Bad("Give the day the police were told. It cannot be a day that has not come yet.");
        }

        if (request.EstimatedValue is { } value && (value < 0 || value > MaxValue))
        {
            return Bad("The value must be an amount in rupees, not below zero.");
        }

        return (new Fields(
            (LossKind)request.Kind,
            noticed,
            request.LastSeenOn,
            Blank(request.LastSeenPlace),
            description,
            Blank(request.LastHandledBy),
            Blank(request.PoliceReportNumber),
            request.PoliceReportedOn,
            request.EstimatedValue is { } v ? decimal.Round(v, 2) : null), null);
    }

    /// <summary>Writes what the review says onto the loss. Null when it is fine, otherwise the refusal.</summary>
    private static IResult? ApplyReview(EquipmentLoss loss, LossReviewRequest request)
    {
        if (request.Findings?.Length > MaxLongTextLength || request.CorrectiveAction?.Length > MaxLongTextLength)
        {
            return Results.BadRequest(new { error = $"What was found out, and what is being done, can each be at most {MaxLongTextLength} characters." });
        }

        if (request.PoliceReportNumber?.Trim().Length > 100)
        {
            return Results.BadRequest(new { error = "The police complaint number can be at most 100 characters." });
        }

        if (request.EstimatedValue is { } value && (value < 0 || value > MaxValue))
        {
            return Results.BadRequest(new { error = "The value must be an amount in rupees, not below zero." });
        }

        if (request.PoliceReportedOn is { } told && told.Year < 2000)
        {
            return Results.BadRequest(new { error = "Give the day the police were told." });
        }

        // What is sent replaces what was there, including a blank, so a finding can be taken out as well as put in.
        if (request.Findings is not null)
        {
            loss.Findings = Blank(request.Findings);
        }

        if (request.CorrectiveAction is not null)
        {
            loss.CorrectiveAction = Blank(request.CorrectiveAction);
        }

        if (request.PoliceReportNumber is not null)
        {
            loss.PoliceReportNumber = Blank(request.PoliceReportNumber);
        }

        loss.PoliceReportedOn = request.PoliceReportedOn ?? loss.PoliceReportedOn;
        loss.EstimatedValue = request.EstimatedValue is { } v ? decimal.Round(v, 2) : loss.EstimatedValue;

        return null;
    }

    private static IResult? ApplyFindings(EquipmentLoss loss, LossCloseRequest request) =>
        ApplyReview(loss, new LossReviewRequest(request.Findings, request.CorrectiveAction, null, null, null));

    // ---------------------------------------------------------------- small things

    private static string? StatusFilter(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" => string.Empty,
        "open" => "open",
        "engineer" => "engineer",
        "reported" => "reported",
        "investigating" => "investigating",
        "awaiting" => "awaiting",
        "recovered" => "recovered",
        "written-off" => "written-off",
        "cancelled" => "cancelled",
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
