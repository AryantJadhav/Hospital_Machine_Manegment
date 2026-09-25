using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using HospitalPm.Domain.Checklists;
using HospitalPm.Infrastructure.Maintenance;
using HospitalPm.Infrastructure.Persistence;
using HospitalPm.Infrastructure.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HospitalPm.Infrastructure.Export;

public sealed record ExportOptions(bool IncludeSignatures = false);

/// <summary>
/// Everything a hospital has recorded, as files it can open without us.
///
/// A ZIP of plain CSV (opens in Excel) and JSON (the checklists), with a README
/// that says what each file is. It exists so that the answer to "what happens to
/// our data if we stop using this" is "you take it with you", and so that the
/// records outlive the software that wrote them.
///
/// Written straight to the output a batch at a time, so a hospital with years of
/// history does not need memory for all of it. A download that is cut short has
/// no manifest.json, which is written last; a complete one lists every file with
/// its row count.
///
/// Not included, on purpose: password hashes and sign-in tokens, the licence, the
/// database credentials, and the audit trail, which stays in the database and its
/// backups.
/// </summary>
public sealed class DataExportService(
    HospitalPmDbContext db,
    HospitalClock clock,
    IOptions<ReportOptions> report)
{
    private const int Batch = 2000;

    /// <summary>Completions carry their answers, so they are read in smaller pieces.</summary>
    private const int CompletionBatch = 500;

    private static readonly JsonSerializerOptions Pretty = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record FileNote(string Name, int Rows, string Description);

    public async Task WriteAsync(Stream output, string generatedBy, ExportOptions options, CancellationToken ct)
    {
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var notes = new List<FileNote>();
        var generatedAtUtc = clock.UtcNow();

        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        string Person(int? id) => id is { } i && users.TryGetValue(i, out var n) ? n : string.Empty;

        var definitions = await LoadDefinitionsAsync(ct);

        notes.Add(await LocationsAsync(zip, ct));
        notes.Add(await EquipmentAsync(zip, ct));
        notes.AddRange(await ChecklistsAsync(zip, ct));
        notes.Add(await SchedulesAsync(zip, ct));
        notes.Add(await TasksAsync(zip, Person, ct));
        notes.Add(await CompletionsAsync(zip, Person, definitions, ct));
        notes.Add(await AnswersAsync(zip, definitions, ct));

        if (options.IncludeSignatures)
        {
            notes.Add(await SignaturesAsync(zip, ct));
        }

        notes.Add(await WorkOrdersAsync(zip, Person, ct));
        notes.Add(await WorkOrderNotesAsync(zip, Person, ct));
        notes.Add(await StaffAsync(zip, ct));

        await WriteTextAsync(zip, "README.txt", Readme(notes, generatedBy, generatedAtUtc, options), ct);

        // Last, so that its presence says the rest arrived.
        await WriteTextAsync(zip, "manifest.json", JsonSerializer.Serialize(new
        {
            hospital = report.Value.HospitalName,
            generatedAt = Instant(generatedAtUtc),
            generatedBy,
            timeZone = ReportTime.Zone(clock.Offset),
            includesSignatures = options.IncludeSignatures,
            files = notes.Select(n => new { file = n.Name, rows = n.Rows, description = n.Description }),
        }, Pretty), ct);
    }

    // ---- Formatting -----------------------------------------------------------

    /// <summary>2026-09-20T14:05:00+05:30: unambiguous, sorts correctly, and Excel and every language read it.</summary>
    private string Instant(DateTime? utc) => utc is null
        ? string.Empty
        : new DateTimeOffset(DateTime.SpecifyKind(utc.Value + clock.Offset, DateTimeKind.Unspecified), clock.Offset)
            .ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture);

    private static string Day(DateOnly? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Number(int? n) => n?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    // ---- One CSV file ---------------------------------------------------------

    private sealed class Sheet : IAsyncDisposable
    {
        private readonly StreamWriter _writer;

        public int Rows { get; private set; }

        public Sheet(ZipArchive zip, string name, string[] headers)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
            // A byte order mark, so Excel reads names and notes as UTF-8.
            _writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _writer.Write(Csv.Line(headers) + "\r\n");
        }

        public void Row(params string?[] cells)
        {
            _writer.Write(Csv.Line(cells) + "\r\n");
            Rows++;
        }

        public ValueTask DisposeAsync() => _writer.DisposeAsync();
    }

    /// <summary>Walks a table in id order, a batch at a time.</summary>
    private static async Task EachBatchAsync<T>(
        Func<int, Task<List<T>>> fetch, Func<T, int> id, Action<T> row)
    {
        var last = 0;
        while (true)
        {
            var batch = await fetch(last);
            if (batch.Count == 0)
            {
                return;
            }

            foreach (var item in batch)
            {
                row(item);
            }

            last = id(batch[^1]);
        }
    }

    // ---- The files ------------------------------------------------------------

    private async Task<FileNote> LocationsAsync(ZipArchive zip, CancellationToken ct)
    {
        var all = await db.Locations.AsNoTracking()
            .Select(l => new { l.Id, l.ParentId, l.Code, l.Name, l.Level, l.IsActive })
            .ToListAsync(ct);

        var byId = all.ToDictionary(l => l.Id);

        string FullName(int id)
        {
            var names = new List<string>();
            for (int? at = id; at is { } i && byId.TryGetValue(i, out var l); at = l.ParentId)
            {
                names.Add(l.Name);
            }

            names.Reverse();
            return string.Join(" > ", names);
        }

        await using var sheet = new Sheet(zip, "locations.csv",
            ["Code", "Name", "Level", "Parent Code", "Full Name", "Active"]);

        foreach (var l in all.OrderBy(l => l.Id))
        {
            sheet.Row(
                l.Code, l.Name, l.Level.ToString(),
                l.ParentId is { } p && byId.TryGetValue(p, out var parent) ? parent.Code : string.Empty,
                FullName(l.Id), l.IsActive ? "Yes" : "No");
        }

        return new FileNote("locations.csv", sheet.Rows,
            "The tree of places: organisation, sites, buildings, floors, departments, rooms.");
    }

    private async Task<FileNote> EquipmentAsync(ZipArchive zip, CancellationToken ct)
    {
        // The first eleven columns are the ones the import template uses, in its
        // order, so this file can be opened in Excel, saved as .xlsx and imported
        // into another installation. Criticality comes last so those stay put.
        await using var sheet = new Sheet(zip, "equipment.csv",
        [
            "Asset Tag", "Serial Number", "Equipment Type", "Location", "Manufacturer", "Model", "Status",
            "Purchase Date", "Installation Date", "Warranty Expiry", "Notes",
            "Equipment Type Code", "Location Name", "Created", "Last Changed",
            "Criticality",
        ]);

        await EachBatchAsync(
            last => db.Equipment.AsNoTracking()
                .Where(e => e.Id > last).OrderBy(e => e.Id).Take(Batch)
                .Select(e => new
                {
                    e.Id, e.AssetTag, e.SerialNumber,
                    Type = e.EquipmentType!.Name, TypeCode = e.EquipmentType!.Code,
                    LocationCode = e.Location!.Code, LocationName = e.Location!.Name,
                    e.Manufacturer, e.Model, e.Status,
                    e.PurchaseDate, e.InstallationDate, e.WarrantyExpiryDate, e.Notes,
                    e.CreatedAtUtc, e.UpdatedAtUtc, e.Criticality,
                }).ToListAsync(ct),
            e => e.Id,
            e => sheet.Row(
                e.AssetTag, e.SerialNumber, e.Type, e.LocationCode, e.Manufacturer, e.Model, e.Status.ToString(),
                Day(e.PurchaseDate), Day(e.InstallationDate), Day(e.WarrantyExpiryDate), e.Notes,
                e.TypeCode, e.LocationName, Instant(e.CreatedAtUtc), Instant(e.UpdatedAtUtc),
                e.Criticality?.ToString()));

        return new FileNote("equipment.csv", sheet.Rows,
            "The equipment register. The first eleven columns match the import template.");
    }

    private async Task<List<FileNote>> ChecklistsAsync(ZipArchive zip, CancellationToken ct)
    {
        var versions = await db.ChecklistTemplateVersions.AsNoTracking()
            .OrderBy(v => v.ChecklistTemplateId).ThenBy(v => v.VersionNo)
            .Select(v => new
            {
                v.Id, v.VersionNo, v.Status, v.ChangeNote, v.PublishedAtUtc, v.Definition,
                Code = v.Template!.Code, Name = v.Template!.Name, Type = v.Template!.EquipmentType!.Name,
            })
            .ToListAsync(ct);

        var files = 0;

        // One file per version, kept whole. A completed PM is read against the
        // version it was filled under, and these are the questions it asked.
        foreach (var v in versions)
        {
            var entry = zip.CreateEntry($"checklists/{v.Code}-v{v.VersionNo}.json", CompressionLevel.Fastest);
            await using var stream = entry.Open();
            await JsonSerializer.SerializeAsync(stream, new
            {
                code = v.Code, name = v.Name, equipmentType = v.Type, version = v.VersionNo,
                status = v.Status.ToString(), changeNote = v.ChangeNote,
                publishedAt = Instant(v.PublishedAtUtc), definition = v.Definition,
            }, Pretty, ct);
            files++;
        }

        await using var sheet = new Sheet(zip, "checklists.csv",
            ["Code", "Name", "Equipment Type", "Version", "Status", "Published", "Change Note", "File"]);

        foreach (var v in versions)
        {
            sheet.Row(
                v.Code, v.Name, v.Type, Number(v.VersionNo), v.Status.ToString(),
                Instant(v.PublishedAtUtc), v.ChangeNote, $"checklists/{v.Code}-v{v.VersionNo}.json");
        }

        return
        [
            new FileNote("checklists.csv", sheet.Rows, "Every checklist and every version of it, with the file holding its questions."),
            new FileNote("checklists/", files, "One JSON file per checklist version: the sections, questions, units and limits."),
        ];
    }

    private async Task<FileNote> SchedulesAsync(ZipArchive zip, CancellationToken ct)
    {
        await using var sheet = new Sheet(zip, "pm_schedules.csv",
            ["Asset Tag", "Checklist", "Frequency", "Interval Days", "Anchor Date", "Grace Days", "Active"]);

        await EachBatchAsync(
            last => db.PmSchedules.AsNoTracking()
                .Where(s => s.Id > last).OrderBy(s => s.Id).Take(Batch)
                .Select(s => new
                {
                    s.Id, Tag = s.Equipment!.AssetTag, Checklist = s.ChecklistTemplate!.Name,
                    s.Frequency, s.IntervalDays, s.AnchorDate, s.GraceDays, s.IsActive,
                }).ToListAsync(ct),
            s => s.Id,
            s => sheet.Row(
                s.Tag, s.Checklist, s.Frequency.ToString(), Number(s.IntervalDays > 0 ? s.IntervalDays : null),
                Day(s.AnchorDate), Number(s.GraceDays), s.IsActive ? "Yes" : "No"));

        return new FileNote("pm_schedules.csv", sheet.Rows, "Which machine gets which checklist, how often.");
    }

    private async Task<FileNote> TasksAsync(ZipArchive zip, Func<int?, string> person, CancellationToken ct)
    {
        await using var sheet = new Sheet(zip, "pm_tasks.csv",
            ["Task Id", "Asset Tag", "Checklist", "Due Date", "Status", "Completed", "Completed By", "Skip Reason"]);

        await EachBatchAsync(
            last => db.PmTasks.AsNoTracking()
                .Where(t => t.Id > last).OrderBy(t => t.Id).Take(Batch)
                .Select(t => new
                {
                    t.Id, Tag = t.Equipment!.AssetTag, Checklist = t.Schedule!.ChecklistTemplate!.Name,
                    t.DueDate, t.Status, t.CompletedAtUtc, t.CompletedByUserId, t.SkipReason,
                }).ToListAsync(ct),
            t => t.Id,
            t => sheet.Row(
                Number(t.Id), t.Tag, t.Checklist, Day(t.DueDate), t.Status.ToString(),
                Instant(t.CompletedAtUtc), person(t.CompletedByUserId), t.SkipReason));

        return new FileNote("pm_tasks.csv",
            sheet.Rows, "Every PM that fell due: when, and whether it was completed, skipped or is still open.");
    }

    private async Task<Dictionary<int, (int Version, Dictionary<string, (string Section, string Label, string? Unit)> Items)>>
        LoadDefinitionsAsync(CancellationToken ct)
    {
        var rows = await db.ChecklistTemplateVersions.AsNoTracking()
            .Select(v => new { v.Id, v.VersionNo, v.Definition })
            .ToListAsync(ct);

        return rows.ToDictionary(
            v => v.Id,
            v => (v.VersionNo, v.Definition.Sections
                .SelectMany(s => s.Items.Select(i => (i.Key, Section: s.Title, i.Label, i.Unit)))
                .GroupBy(i => i.Key)
                .ToDictionary(g => g.Key, g => (g.First().Section, g.First().Label, g.First().Unit))));
    }

    private async Task<FileNote> CompletionsAsync(
        ZipArchive zip,
        Func<int?, string> person,
        Dictionary<int, (int Version, Dictionary<string, (string Section, string Label, string? Unit)> Items)> definitions,
        CancellationToken ct)
    {
        await using var sheet = new Sheet(zip, "pm_completions.csv",
        [
            "Task Id", "Asset Tag", "Checklist", "Checklist Version", "Due Date", "Performed", "Recorded",
            "Performed By", "Signed Name", "Notes", "Out Of Range", "Failed Checks",
        ]);

        await EachBatchAsync(
            last => db.PmCompletions.AsNoTracking()
                .Where(c => c.Id > last).OrderBy(c => c.Id).Take(CompletionBatch)
                .Select(c => new
                {
                    c.Id, c.PmTaskId, c.ChecklistTemplateVersionId, c.Answers,
                    Tag = c.Task!.Equipment!.AssetTag, Checklist = c.Task!.Schedule!.ChecklistTemplate!.Name,
                    Due = c.Task!.DueDate, c.PerformedAtUtc, c.CompletedAtUtc,
                    c.CompletedByUserId, c.SignedByName, c.Notes,
                }).ToListAsync(ct),
            c => c.Id,
            c => sheet.Row(
                Number(c.PmTaskId), c.Tag, c.Checklist,
                Number(definitions.TryGetValue(c.ChecklistTemplateVersionId, out var d) ? d.Version : null),
                Day(c.Due), Instant(c.PerformedAtUtc ?? c.CompletedAtUtc), Instant(c.CompletedAtUtc),
                person(c.CompletedByUserId), c.SignedByName, c.Notes,
                Number(c.Answers.Count(a => a.Value.OutOfRange)),
                Number(c.Answers.Count(a => a.Value.IsFailedCheck()))));

        return new FileNote("pm_completions.csv", sheet.Rows,
            "One row per completed PM: who did it, when, and how many readings were out of range or checks failed.");
    }

    private async Task<FileNote> AnswersAsync(
        ZipArchive zip,
        Dictionary<int, (int Version, Dictionary<string, (string Section, string Label, string? Unit)> Items)> definitions,
        CancellationToken ct)
    {
        await using var sheet = new Sheet(zip, "pm_answers.csv",
            ["Task Id", "Asset Tag", "Section", "Check", "Answer", "Unit", "Note", "Out Of Range", "Failed"]);

        await EachBatchAsync(
            last => db.PmCompletions.AsNoTracking()
                .Where(c => c.Id > last).OrderBy(c => c.Id).Take(CompletionBatch)
                .Select(c => new
                {
                    c.Id, c.PmTaskId, c.ChecklistTemplateVersionId, c.Answers,
                    Tag = c.Task!.Equipment!.AssetTag,
                }).ToListAsync(ct),
            c => c.Id,
            c =>
            {
                definitions.TryGetValue(c.ChecklistTemplateVersionId, out var d);

                foreach (var (key, answer) in c.Answers)
                {
                    // A question the checklist no longer names is still exported, under its key.
                    (string Section, string Label, string? Unit) meta =
                        d.Items is not null && d.Items.TryGetValue(key, out var found)
                            ? found
                            : (string.Empty, key, null);

                    sheet.Row(
                        Number(c.PmTaskId), c.Tag, meta.Section, meta.Label, answer.Value, meta.Unit, answer.Note,
                        answer.OutOfRange ? "Yes" : string.Empty, answer.IsFailedCheck() ? "Yes" : string.Empty);
                }
            });

        return new FileNote("pm_answers.csv", sheet.Rows,
            "Every answer on every completed PM, one per row, with the question it answered.");
    }

    private async Task<FileNote> SignaturesAsync(ZipArchive zip, CancellationToken ct)
    {
        var count = 0;

        await EachBatchAsync(
            last => db.PmCompletions.AsNoTracking()
                .Where(c => c.Id > last && c.Signature != null).OrderBy(c => c.Id).Take(100)
                .Select(c => new { c.Id, c.PmTaskId, c.Signature, c.SignatureFormat }).ToListAsync(ct),
            c => c.Id,
            c =>
            {
                var extension = string.Equals(c.SignatureFormat, "svg", StringComparison.OrdinalIgnoreCase) ? "svg" : "png";
                var entry = zip.CreateEntry($"signatures/task-{c.PmTaskId}.{extension}", CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(c.Signature!);
                count++;
            });

        return new FileNote("signatures/", count, "The technician's signature for each completed PM, named by Task Id.");
    }

    private async Task<FileNote> WorkOrdersAsync(ZipArchive zip, Func<int?, string> person, CancellationToken ct)
    {
        await using var sheet = new Sheet(zip, "work_orders.csv",
        [
            "Number", "Asset Tag", "Status", "Priority", "Fault", "Reported By", "Reported",
            "Assigned To", "Assigned", "Started", "Resolved By", "Resolved", "Resolution", "Closed",
            "Out Of Service", "Back In Service", "Hours Out Of Service",
        ]);

        await EachBatchAsync(
            last => db.WorkOrders.AsNoTracking()
                .Where(w => w.Id > last).OrderBy(w => w.Id).Take(Batch)
                .Select(w => new
                {
                    w.Id, w.Number, Tag = w.Equipment!.AssetTag, w.Status, w.Priority, w.FaultDescription,
                    w.ReportedByUserId, w.ReportedAtUtc, w.AssignedToUserId, w.AssignedAtUtc, w.StartedAtUtc,
                    w.ResolvedByUserId, w.ResolvedAtUtc, w.ResolutionNotes, w.ClosedAtUtc,
                    w.OutOfServiceAtUtc, w.BackInServiceAtUtc,
                }).ToListAsync(ct),
            w => w.Id,
            w => sheet.Row(
                w.Number, w.Tag, w.Status.ToString(), w.Priority.ToString(), w.FaultDescription,
                person(w.ReportedByUserId), Instant(w.ReportedAtUtc),
                person(w.AssignedToUserId), Instant(w.AssignedAtUtc), Instant(w.StartedAtUtc),
                person(w.ResolvedByUserId), Instant(w.ResolvedAtUtc), w.ResolutionNotes, Instant(w.ClosedAtUtc),
                Instant(w.OutOfServiceAtUtc), Instant(w.BackInServiceAtUtc),
                w.OutOfServiceAtUtc is { } from && w.BackInServiceAtUtc is { } to
                    ? Math.Round((to - from).TotalHours, 1).ToString("0.0", CultureInfo.InvariantCulture)
                    : string.Empty));

        return new FileNote("work_orders.csv", sheet.Rows,
            "Every fault: who reported and fixed it, the times, and how long the machine was out of service.");
    }

    private async Task<FileNote> WorkOrderNotesAsync(ZipArchive zip, Func<int?, string> person, CancellationToken ct)
    {
        await using var sheet = new Sheet(zip, "work_order_notes.csv",
            ["Work Order", "Written", "Written By", "Note", "Status After"]);

        await EachBatchAsync(
            last => db.WorkOrderNotes.AsNoTracking()
                .Where(n => n.Id > last).OrderBy(n => n.Id).Take(Batch)
                .Select(n => new
                {
                    n.Id, Number = n.WorkOrder!.Number, n.CreatedAtUtc, n.AuthorUserId, n.Body, n.StatusAfter,
                }).ToListAsync(ct),
            n => n.Id,
            n => sheet.Row(
                n.Number, Instant(n.CreatedAtUtc), person(n.AuthorUserId), n.Body, n.StatusAfter?.ToString()));

        return new FileNote("work_order_notes.csv", sheet.Rows, "The running notes on each fault.");
    }

    private async Task<FileNote> StaffAsync(ZipArchive zip, CancellationToken ct)
    {
        var roles = await (from ur in db.UserRoles
                           join r in db.Roles on ur.RoleId equals r.Id
                           select new { ur.UserId, r.Name })
            .AsNoTracking().ToListAsync(ct);

        var byUser = roles.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => string.Join("; ", g.Select(r => r.Name)));

        // Names, roles and whether the account is active. No passwords, hashes
        // or tokens: this file is for records, not for signing in.
        await using var sheet = new Sheet(zip, "staff.csv",
            ["Full Name", "User Name", "Staff Code", "Role", "Active", "Created", "Last Sign-in"]);

        var users = await db.Users.AsNoTracking().OrderBy(u => u.Id)
            .Select(u => new { u.Id, u.FullName, u.UserName, u.StaffCode, u.IsActive, u.CreatedAtUtc, u.LastLoginAtUtc })
            .ToListAsync(ct);

        foreach (var u in users)
        {
            sheet.Row(
                u.FullName, u.UserName, u.StaffCode, byUser.GetValueOrDefault(u.Id, string.Empty),
                u.IsActive ? "Yes" : "No", Instant(u.CreatedAtUtc), Instant(u.LastLoginAtUtc));
        }

        return new FileNote("staff.csv", sheet.Rows, "The people who use the system, and their role.");
    }

    // ---- The covering notes ---------------------------------------------------

    private async Task WriteTextAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        await using var stream = entry.Open();
        await stream.WriteAsync(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text), ct);
    }

    private string Readme(List<FileNote> notes, string generatedBy, DateTime generatedAtUtc, ExportOptions options)
    {
        var zone = ReportTime.Zone(clock.Offset);
        var hospital = string.IsNullOrWhiteSpace(report.Value.HospitalName) ? "Hospital PM" : report.Value.HospitalName;
        var sb = new StringBuilder();

        sb.AppendLine($"{hospital}: data export");
        sb.AppendLine($"Prepared {ReportTime.DateTime(generatedAtUtc, clock.Offset)} {zone} by {generatedBy}.");
        sb.AppendLine();
        sb.AppendLine("This is a copy of everything recorded in Hospital PM, as ordinary files. You do not need");
        sb.AppendLine("Hospital PM, or us, to read it.");
        sb.AppendLine();
        sb.AppendLine("FILES");

        foreach (var n in notes)
        {
            sb.AppendLine($"  {n.Name,-24} {n.Rows,8} {(n.Name.EndsWith('/') ? "files" : "rows ")}  {n.Description}");
        }

        sb.AppendLine();
        sb.AppendLine("HOW TO READ THEM");
        sb.AppendLine("  .csv files open in Excel, LibreOffice or Google Sheets. They are UTF-8.");
        sb.AppendLine($"  Dates are yyyy-mm-dd. Times are ISO 8601 with the {zone} offset, for example 2026-09-20T14:05:00+05:30.");
        sb.AppendLine("  Status and priority are written as words. A machine is always identified by its Asset Tag.");
        sb.AppendLine("  A PM is identified by its Task Id, which links pm_tasks, pm_completions and pm_answers.");
        sb.AppendLine("  A cell that starts with an apostrophe was text that a spreadsheet would otherwise have read as a formula.");
        sb.AppendLine();
        sb.AppendLine("MOVING THE REGISTER ELSEWHERE");
        sb.AppendLine("  The first eleven columns of equipment.csv, and the first four of locations.csv, match the");
        sb.AppendLine("  Hospital PM import templates. Open the file in Excel, save it as .xlsx, and import it.");
        sb.AppendLine();
        sb.AppendLine("NOT INCLUDED");
        sb.AppendLine("  Passwords, sign-in tokens, the licence, and the audit trail. The audit trail stays in the");
        sb.AppendLine("  database and in its backups.");

        if (!options.IncludeSignatures)
        {
            sb.AppendLine("  Signature images. Ask for the export with signatures to include them.");
        }

        sb.AppendLine();
        sb.AppendLine("IS IT COMPLETE?");
        sb.AppendLine("  manifest.json is written last and lists every file with its row count. If it is missing,");
        sb.AppendLine("  the download was cut short: take it again.");

        return sb.ToString();
    }
}
