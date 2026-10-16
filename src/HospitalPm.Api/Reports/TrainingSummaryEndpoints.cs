using System.Text;
using HospitalPm.Api.Auth;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using HospitalPm.Infrastructure.Reports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HospitalPm.Api.Reports;

/// <summary>
/// The training the department gave in a period: sessions, who came, on what, and for how long.
///
/// Administrators only, like the other fleet reports. A session belongs to the day it was held, at the hospital. Not
/// filtered by place: a session is about a kind of machine or a subject, not about a ward. The people are staff;
/// nothing here concerns a patient.
/// </summary>
public static class TrainingSummaryEndpoints
{
    public static void MapTrainingSummaryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports/training")
            .WithTags("Reports")
            .RequirePermission(Permissions.ReportsView);

        group.MapGet("/", SummaryAsync);
        group.MapGet("/report.csv", CsvAsync);
    }

    private static async Task<IResult> SummaryAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken ct)
    {
        var (scope, report, error) = await BuildAsync(db, clock, from, to, ct);
        if (error is not null)
        {
            return error;
        }

        return Results.Ok(new
        {
            from = report!.From,
            to = report.To,
            countedThrough = scope!.CountedThrough,
            scope = scope.Scope,
            held = report.Held,
            planned = report.Planned,
            attendances = report.Attendances,
            peopleTrained = report.PeopleTrained,
            hours = report.Hours,
            personHours = report.PersonHours,
            withoutDuration = report.WithoutDuration,
            byType = report.ByType,
            byPerson = report.People,
            items = report.Sessions.Select(s => new
            {
                s.Id,
                s.Title,
                s.Date,
                s.Planned,
                s.EquipmentType,
                s.EquipmentId,
                s.AssetTag,
                trainerType = Domain.Training.TrainerKind.Label(s.TrainerType),
                s.Trainer,
                s.Venue,
                s.DurationMinutes,
                s.Attendees,
                s.AttendeeNames,
            }),
        });
    }

    private static async Task<IResult> CsvAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken ct)
    {
        var (_, report, error) = await BuildAsync(db, clock, from, to, ct);
        if (error is not null)
        {
            return error;
        }

        return Results.File(
            Encoding.UTF8.GetBytes(TrainingSummaryReport.ToCsv(report!)),
            "text/csv; charset=utf-8",
            $"training-{report!.From:yyyyMMdd}-{report.To:yyyyMMdd}.csv");
    }

    private static async Task<(ReportScope? Scope, TrainingSummaryData? Report, IResult? Error)> BuildAsync(
        HospitalPmDbContext db,
        HospitalClock clock,
        DateOnly? from,
        DateOnly? to,
        CancellationToken ct)
    {
        // Training is occasional and assessed by the year, so the financial year so far when nothing is asked for
        // (the other reports start at the month).
        var today = clock.Today();
        var fyStart = new DateOnly(today.Month >= 4 ? today.Year : today.Year - 1, 4, 1);
        var (scope, error) = await ReportScope.ResolveAsync(db, clock, from ?? fyStart, to ?? today, null, ct);
        if (error is not null)
        {
            return (null, null, error);
        }

        var firstDay = scope!.From;
        var lastDay = scope.To;

        var rows = await db.TrainingSessions.AsNoTracking()
            .Where(s => s.SessionDate >= firstDay && s.SessionDate <= lastDay)
            .Select(s => new
            {
                s.Id,
                s.Title,
                s.SessionDate,
                Type = s.EquipmentType != null ? s.EquipmentType.Name : null,
                s.EquipmentId,
                Tag = s.Machine != null ? s.Machine.AssetTag : null,
                s.TrainerType,
                s.Trainer,
                s.Venue,
                s.DurationMinutes,
                Attendees = s.Attendees.Select(a => new { a.UserId, a.Name, a.Designation }).ToList(),
            })
            .ToListAsync(ct);

        var inputs = rows.Select(r => new TrainingSessionInput(
            r.Id,
            r.Title,
            r.SessionDate,
            r.Type,
            r.EquipmentId,
            r.Tag,
            r.TrainerType,
            r.Trainer,
            r.Venue,
            r.DurationMinutes,
            r.Attendees.Select(a => new TrainingAttendeeInput(a.UserId, a.Name, a.Designation)).ToList()));

        return (scope, TrainingSummaryReport.Build(inputs, firstDay, lastDay, today), null);
    }
}
