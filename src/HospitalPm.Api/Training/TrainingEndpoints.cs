using HospitalPm.Api.Auth;
using System.Security.Claims;
using HospitalPm.Api.Equipment;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Training;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;

// See EquipmentEndpoints: these expressions are LINQ-to-SQL, where ToLower() is
// what translates and the StringComparison overloads CA1862 asks for do not.
#pragma warning disable CA1862

namespace HospitalPm.Api.Training;

/// <summary>One person on a session's list. Give <see cref="UserId"/> for someone with an account, or just a name.</summary>
public sealed record TrainingAttendeeRequest(int? UserId, string? Name, string? Designation);

public sealed record TrainingSessionRequest(
    // Optional; left out, a session is called "Training session".
    string? Title,
    DateOnly SessionDate,
    int? EquipmentTypeId,
    // The machine the training was given on. Its name, company and model are read from its record.
    int? EquipmentId,
    // "Vendor" (the vendor or manufacturer) or "InHouse" (our own team); left out when not said.
    string? TrainerType,
    string? Trainer,
    string? Venue,
    int? DurationMinutes,
    string? Notes,
    // The whole list. On an edit it replaces what was there, so a person left off is taken off.
    List<TrainingAttendeeRequest>? Attendees);

/// <summary>
/// The biomedical department's training sessions and who attended them.
///
/// Everyone signed in can read it - an engineer finding out who was trained on a machine before
/// leaving it with them is the point of keeping it. Only an Administrator adds, changes or removes
/// a session, the same split as spare parts and checklists.
///
/// The people are staff. Nothing here is about a patient.
/// </summary>
public static class TrainingEndpoints
{
    private const int MaxPageSize = 200;
    private const int MaxTextLength = 200;
    private const int MaxNotesLength = 4000;
    private const int MaxAttendees = 500;

    /// <summary>What a session with no name is called.</summary>
    public const string DefaultTitle = "Training session";

    public static void MapTrainingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/training").WithTags("Training").RequirePermission(Permissions.TrainingView);

        group.MapGet("/", ListAsync);
        group.MapGet("/people", PeopleAsync);
        group.MapGet("/{id:int}", GetAsync);
        group.MapGet("/{id:int}/report.pdf", ReportAsync);

        group.MapPost("/", CreateAsync).RequirePermission(Permissions.TrainingEdit);
        group.MapPut("/{id:int}", UpdateAsync).RequirePermission(Permissions.TrainingEdit);
        group.MapDelete("/{id:int}", DeleteAsync).RequirePermission(Permissions.TrainingEdit);
    }

    private static async Task<IResult> ListAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        [FromQuery] string? q,
        [FromQuery] int? equipmentTypeId,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.TrainingSessions.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(q))
        {
            // By what it was about, who ran it, where, or who came: what a person has in their head
            // when they go looking for a session.
            var term = q.Trim().ToLower();
            query = query.Where(s =>
                s.Title.ToLower().Contains(term) ||
                (s.Trainer != null && s.Trainer.ToLower().Contains(term)) ||
                (s.Venue != null && s.Venue.ToLower().Contains(term)) ||
                s.Attendees.Any(a => a.Name.ToLower().Contains(term)) ||
                // By the machine too: its number, who made it, or its model or serial number.
                (s.Machine != null &&
                    (s.Machine.AssetTag.ToLower().Contains(term) ||
                     (s.Machine.Manufacturer != null && s.Machine.Manufacturer.ToLower().Contains(term)) ||
                     (s.Machine.Model != null && s.Machine.Model.ToLower().Contains(term)) ||
                     (s.Machine.SerialNumber != null && s.Machine.SerialNumber.ToLower().Contains(term)))));
        }

        if (equipmentTypeId is not null)
        {
            query = query.Where(s => s.EquipmentTypeId == equipmentTypeId);
        }

        if (from is not null)
        {
            query = query.Where(s => s.SessionDate >= from);
        }

        if (to is not null)
        {
            query = query.Where(s => s.SessionDate <= to);
        }

        var total = await query.CountAsync(ct);
        var today = clock.Today();

        var rows = await query
            .OrderByDescending(s => s.SessionDate)
            .ThenByDescending(s => s.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new
            {
                s.Id,
                s.Title,
                s.SessionDate,
                s.EquipmentTypeId,
                EquipmentTypeName = s.EquipmentType == null ? null : s.EquipmentType.Name,
                s.EquipmentId,
                AssetTag = s.Machine == null ? null : s.Machine.AssetTag,
                MachineName = s.Machine == null ? null : s.Machine.EquipmentType!.Name,
                Manufacturer = s.Machine == null ? null : s.Machine.Manufacturer,
                Model = s.Machine == null ? null : s.Machine.Model,
                s.TrainerType,
                s.Trainer,
                s.Venue,
                s.DurationMinutes,
                AttendeeCount = s.Attendees.Count,
                SomeAttendees = s.Attendees.OrderBy(a => a.Name).Select(a => a.Name).Take(3).ToList(),
            })
            .ToListAsync(ct);

        var items = rows.Select(r => new
        {
            r.Id,
            r.Title,
            r.SessionDate,
            r.EquipmentTypeId,
            r.EquipmentTypeName,
            r.EquipmentId,
            r.AssetTag,
            r.MachineName,
            r.Manufacturer,
            r.Model,
            r.TrainerType,
            r.Trainer,
            r.Venue,
            r.DurationMinutes,
            r.AttendeeCount,
            r.SomeAttendees,
            // A day still to come is a session that is planned, not one that has been held.
            IsPlanned = r.SessionDate > today,
        });

        return Results.Ok(new { items, total, page, pageSize });
    }

    /// <summary>
    /// Each person who has attended anything, with how many sessions and the last one: who has been
    /// trained, as opposed to which sessions were held.
    /// </summary>
    private static async Task<IResult> PeopleAsync(
        HospitalPmDbContext db, [FromQuery] string? q, CancellationToken ct)
    {
        var rows = await db.TrainingAttendees.AsNoTracking()
            .Select(a => new
            {
                a.UserId,
                a.Name,
                a.Designation,
                a.Session!.SessionDate,
                SessionId = a.TrainingSessionId,
                EquipmentType = a.Session.EquipmentType == null ? null : a.Session.EquipmentType.Name,
            })
            .ToListAsync(ct);

        // One person whether they were written as "Asha Rao" or "asha rao": an account is the surest
        // way to tell, and failing that the name with its case and spacing ignored.
        var people = rows
            .GroupBy(r => r.UserId is { } id ? $"u{id}" : $"n{r.Name.Trim().ToLowerInvariant()}")
            .Select(g =>
            {
                var latest = g.OrderByDescending(r => r.SessionDate).First();
                return new
                {
                    latest.UserId,
                    latest.Name,
                    Designation = g.Select(r => r.Designation).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)),
                    Sessions = g.Select(r => r.SessionId).Distinct().Count(),
                    LastSessionDate = latest.SessionDate,
                    Covered = g.Select(r => r.EquipmentType).Where(t => t is not null).Distinct().Order().ToList(),
                };
            });

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            people = people.Where(p => p.Name.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return Results.Ok(people
            .OrderByDescending(p => p.Sessions)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static async Task<IResult> GetAsync(int id, HospitalPmDbContext db, HospitalClock clock, CancellationToken ct)
    {
        var s = await db.TrainingSessions.AsNoTracking()
            .Include(x => x.EquipmentType)
            .Include(x => x.Attendees)
            .Include(x => x.Machine).ThenInclude(m => m!.EquipmentType)
            .Include(x => x.Machine).ThenInclude(m => m!.Location)
            .SingleOrDefaultAsync(x => x.Id == id, ct);

        if (s is null)
        {
            return Results.NotFound();
        }

        var createdBy = await db.Users.AsNoTracking()
            .Where(u => u.Id == s.CreatedByUserId).Select(u => u.FullName).FirstOrDefaultAsync(ct);

        return Results.Ok(new
        {
            s.Id,
            // What the printed report calls it, so the screen and the paper quote the same thing.
            Reference = HospitalPm.Infrastructure.Reports.TrainingReportData.ReferenceFor(s.SessionDate, s.Id),
            s.Title,
            s.SessionDate,
            s.EquipmentTypeId,
            EquipmentTypeName = s.EquipmentType?.Name,
            s.EquipmentId,
            // The machine as its own record has it, so nothing is typed twice and the ten machines of one
            // model are told apart by their numbers.
            Machine = s.Machine is null
                ? null
                : new
                {
                    s.Machine.Id,
                    s.Machine.AssetTag,
                    MachineName = s.Machine.EquipmentType?.Name,
                    s.Machine.Manufacturer,
                    s.Machine.Model,
                    s.Machine.SerialNumber,
                    LocationName = s.Machine.Location?.Name,
                },
            s.TrainerType,
            s.Trainer,
            s.Venue,
            s.DurationMinutes,
            s.Notes,
            IsPlanned = s.SessionDate > clock.Today(),
            CreatedByName = createdBy,
            s.CreatedAtUtc,
            Attendees = s.Attendees
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(a => new { a.Id, a.UserId, a.Name, a.Designation })
                .ToList(),
        });
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] TrainingSessionRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var (attendees, error) = await ValidateAsync(request, db, clock, ct);
        if (error is not null)
        {
            return error;
        }

        var session = new TrainingSession
        {
            Title = TitleOf(request),
            SessionDate = request.SessionDate,
            EquipmentId = request.EquipmentId,
            EquipmentTypeId = await TypeAsync(request, db, ct),
            TrainerType = Blank(request.TrainerType),
            Trainer = Blank(request.Trainer),
            Venue = Blank(request.Venue),
            DurationMinutes = request.DurationMinutes,
            Notes = Blank(request.Notes),
            CreatedByUserId = EquipmentMoveEndpoints.UserId(principal),
            Attendees = attendees,
        };

        db.TrainingSessions.Add(session);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/training/{session.Id}", new { session.Id });
    }

    private static async Task<IResult> UpdateAsync(
        int id,
        [FromBody] TrainingSessionRequest request,
        HospitalPmDbContext db,
        HospitalClock clock,
        CancellationToken ct)
    {
        var session = await db.TrainingSessions.Include(s => s.Attendees).SingleOrDefaultAsync(s => s.Id == id, ct);
        if (session is null)
        {
            return Results.NotFound();
        }

        var (attendees, error) = await ValidateAsync(request, db, clock, ct);
        if (error is not null)
        {
            return error;
        }

        session.Title = TitleOf(request);
        session.SessionDate = request.SessionDate;
        session.EquipmentId = request.EquipmentId;
        session.EquipmentTypeId = await TypeAsync(request, db, ct);
        session.TrainerType = Blank(request.TrainerType);
        session.Trainer = Blank(request.Trainer);
        session.Venue = Blank(request.Venue);
        session.DurationMinutes = request.DurationMinutes;
        session.Notes = Blank(request.Notes);
        session.UpdatedAtUtc = DateTime.UtcNow;

        // The list as sent replaces the list as it was, in the one save, so a failure leaves the old one.
        db.TrainingAttendees.RemoveRange(session.Attendees);
        session.Attendees = attendees;

        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    /// <summary>
    /// The kind of machine a session is recorded against: that of the machine chosen, when there is one,
    /// so the two cannot disagree; otherwise what was asked for.
    /// </summary>
    private static async Task<int?> TypeAsync(TrainingSessionRequest request, HospitalPmDbContext db, CancellationToken ct)
    {
        if (request.EquipmentId is not { } machineId)
        {
            return request.EquipmentTypeId;
        }

        return await db.Equipment.AsNoTracking()
            .Where(e => e.Id == machineId).Select(e => (int?)e.EquipmentTypeId).FirstOrDefaultAsync(ct);
    }

    /// <summary>The training report as a PDF: the machine, the session, who attended, and a column to sign.</summary>
    private static async Task<IResult> ReportAsync(
        int id,
        HospitalPmDbContext db,
        Microsoft.Extensions.Options.IOptions<HospitalPm.Infrastructure.Reports.ReportOptions> options,
        HospitalClock clock,
        CancellationToken ct)
    {
        var s = await db.TrainingSessions.AsNoTracking()
            .Include(x => x.Attendees)
            .Include(x => x.Machine).ThenInclude(m => m!.EquipmentType)
            .Include(x => x.Machine).ThenInclude(m => m!.Location)
            .SingleOrDefaultAsync(x => x.Id == id, ct);

        if (s is null)
        {
            return Results.NotFound();
        }

        var recordedBy = await db.Users.AsNoTracking()
            .Where(u => u.Id == s.CreatedByUserId).Select(u => u.FullName).FirstOrDefaultAsync(ct);

        // Held or planned is judged on the hospital's own date, as the register does.
        var isPlanned = s.SessionDate > clock.Today();

        var data = new HospitalPm.Infrastructure.Reports.TrainingReportData(
            s.Id,
            s.Title,
            s.SessionDate,
            isPlanned,
            s.Machine?.AssetTag,
            s.Machine?.EquipmentType?.Name,
            s.Machine?.Manufacturer,
            s.Machine?.Model,
            s.Machine?.SerialNumber,
            s.Machine?.Location?.Name,
            s.TrainerType,
            s.Trainer,
            s.Venue,
            s.DurationMinutes,
            s.Notes,
            recordedBy,
            s.Attendees
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(a => new HospitalPm.Infrastructure.Reports.TrainingReportAttendee(a.Name, a.Designation))
                .ToList());

        var pdf = new HospitalPm.Infrastructure.Reports.TrainingReportDocument(data, options.Value).GeneratePdf();

        return Results.File(pdf, "application/pdf", $"{data.Reference}.pdf");
    }

    private static async Task<IResult> DeleteAsync(int id, HospitalPmDbContext db, CancellationToken ct)
    {
        var session = await db.TrainingSessions.SingleOrDefaultAsync(s => s.Id == id, ct);
        if (session is null)
        {
            return Results.NotFound();
        }

        // Its attendees go with it. The removal is in the audit log, written by the database.
        db.TrainingSessions.Remove(session);
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }

    private static async Task<(List<TrainingAttendee> Attendees, IResult? Error)> ValidateAsync(
        TrainingSessionRequest request, HospitalPmDbContext db, HospitalClock clock, CancellationToken ct)
    {
        List<TrainingAttendee> none = [];

        if (TitleOf(request).Length > MaxTextLength)
        {
            return (none, Results.BadRequest(new { error = $"The title can be at most {MaxTextLength} characters." }));
        }

        var today = clock.Today();
        if (request.SessionDate.Year < 2000 || request.SessionDate > today.AddYears(5))
        {
            return (none, Results.BadRequest(new { error = "Give the date of the session." }));
        }

        if (Blank(request.TrainerType) is { } trainerType && !TrainerKind.IsKnown(trainerType))
        {
            return (none, Results.BadRequest(new { error = "The trainer must be from the vendor or manufacturer, or the in-house team." }));
        }

        if (request.Trainer?.Trim().Length > MaxTextLength || request.Venue?.Trim().Length > MaxTextLength)
        {
            return (none, Results.BadRequest(new { error = $"The trainer and the venue can be at most {MaxTextLength} characters." }));
        }

        if (request.Notes?.Length > MaxNotesLength)
        {
            return (none, Results.BadRequest(new { error = $"The notes can be at most {MaxNotesLength} characters." }));
        }

        if (request.DurationMinutes is { } minutes && (minutes < 1 || minutes > 24 * 60))
        {
            return (none, Results.BadRequest(new { error = "The length of the session must be between 1 minute and 24 hours." }));
        }

        if (request.EquipmentId is { } machineId && !await db.Equipment.AnyAsync(e => e.Id == machineId, ct))
        {
            return (none, Results.BadRequest(new { error = "Unknown machine." }));
        }

        if (request.EquipmentTypeId is { } typeId && !await db.EquipmentTypes.AnyAsync(t => t.Id == typeId, ct))
        {
            return (none, Results.BadRequest(new { error = "Unknown equipment type." }));
        }

        var requested = request.Attendees ?? [];
        if (requested.Count > MaxAttendees)
        {
            return (none, Results.BadRequest(new { error = $"A session can list at most {MaxAttendees} people." }));
        }

        var accountIds = requested.Where(a => a.UserId is not null).Select(a => a.UserId!.Value).Distinct().ToList();
        var accountNames = await db.Users.AsNoTracking()
            .Where(u => accountIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

        var attendees = new List<TrainingAttendee>();
        var seenAccounts = new HashSet<int>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in requested)
        {
            string name;

            if (a.UserId is { } userId)
            {
                if (!accountNames.TryGetValue(userId, out var fullName))
                {
                    return (none, Results.BadRequest(new { error = "One of the people chosen is not a known user." }));
                }

                if (!seenAccounts.Add(userId))
                {
                    return (none, Results.BadRequest(new { error = $"{fullName} is listed twice." }));
                }

                // The name as it is on the account now, kept as written: the record does not change
                // if the account is renamed later.
                name = fullName;
            }
            else
            {
                name = a.Name?.Trim() ?? string.Empty;
                if (name.Length == 0)
                {
                    return (none, Results.BadRequest(new { error = "Every person listed needs a name." }));
                }
            }

            if (name.Length > MaxTextLength)
            {
                return (none, Results.BadRequest(new { error = $"A name can be at most {MaxTextLength} characters." }));
            }

            if (!seenNames.Add(name))
            {
                return (none, Results.BadRequest(new { error = $"{name} is listed twice." }));
            }

            if (a.Designation?.Trim().Length > MaxTextLength)
            {
                return (none, Results.BadRequest(new { error = $"A designation can be at most {MaxTextLength} characters." }));
            }

            attendees.Add(new TrainingAttendee { UserId = a.UserId, Name = name, Designation = Blank(a.Designation) });
        }

        return (attendees, null);
    }

    /// <summary>
    /// What the session is called. Optional: a session is mostly known by its date, who ran it and who
    /// came, so one left unnamed is simply a training session.
    /// </summary>
    private static string TitleOf(TrainingSessionRequest request) =>
        string.IsNullOrWhiteSpace(request.Title) ? DefaultTitle : request.Title.Trim();

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
