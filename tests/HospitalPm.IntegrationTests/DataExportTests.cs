using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.Maintenance;
using HospitalPm.Domain.WorkOrders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The full data export: everything a hospital has recorded, as files.
///
/// The pitch promises "your data stays yours, and exports out". These pin what
/// that means: every record is there, the files say what they are, nothing that
/// signs anyone in leaves the building, and a download that was cut short can be
/// told from one that was not.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class DataExportTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "Export2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private string _suffix = null!;
    private string _tag = null!;
    private int _taskId;
    private string _workOrderNumber = null!;

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x01, 0x02, 0x03, 0x04];

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];
        _tag = $"EXP-{_suffix}".ToUpperInvariant();

        _admin = await SignedInAsync($"ex-adm-{_suffix}", "Head, Biomedical", Roles.BmeHead);
        _employee = await SignedInAsync($"ex-emp-{_suffix}", "Ward Technician", Roles.BmeEngineer);

        await SeedAsync();
    }

    private async Task<HttpClient> SignedInAsync(string userName, string fullName, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = userName, FullName = fullName, IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, role);
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
    }

    /// <summary>
    /// One machine with a comma, a quote and a formula-looking start in its notes,
    /// one completed PM with a failed check and a reading out of range and a
    /// signature, and one fault that was out of service for 5.5 hours.
    /// </summary>
    private async Task SeedAsync()
    {
        await using var db = fixture.CreateContext();

        var room = new Location { Code = $"EXR-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
        db.Locations.Add(room);
        await db.SaveChangesAsync();

        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
        var machine = new Domain.Assets.Equipment
        {
            AssetTag = _tag,
            SerialNumber = $"SN-{_suffix}",
            EquipmentTypeId = type.Id,
            LocationId = room.Id,
            Manufacturer = "Hamilton, Inc.",
            Model = "C1",
            Notes = "=1+1, said \"check the hose\"",
        };
        db.Equipment.Add(machine);

        var template = new ChecklistTemplate { EquipmentTypeId = type.Id, Code = $"ex-{_suffix}", Name = "Export PM" };
        db.ChecklistTemplates.Add(template);
        await db.SaveChangesAsync();

        var version = new ChecklistTemplateVersion
        {
            ChecklistTemplateId = template.Id,
            VersionNo = 1,
            Status = ChecklistVersionStatus.Published,
            PublishedAtUtc = DateTime.UtcNow,
            Definition = new ChecklistDefinition
            {
                Sections =
                [
                    new ChecklistSection
                    {
                        Title = "Safety",
                        Items =
                        [
                            new ChecklistItem { Key = "alarm", Label = "Alarm test" },
                            new ChecklistItem
                            {
                                Key = "flow", Label = "Flow rate", Type = ChecklistItemType.Number,
                                Unit = "L/min", Min = 4, Max = 6,
                            },
                        ],
                    },
                ],
            },
        };
        db.ChecklistTemplateVersions.Add(version);
        await db.SaveChangesAsync();

        var schedule = new PmSchedule
        {
            EquipmentId = machine.Id, ChecklistTemplateId = template.Id, Frequency = PmFrequency.Quarterly,
            AnchorDate = new DateOnly(2026, 3, 1), GraceDays = 2,
        };
        db.PmSchedules.Add(schedule);
        await db.SaveChangesAsync();

        var signer = await db.Users.FirstAsync(u => u.UserName == $"ex-emp-{_suffix}");
        // 04:30 UTC is 10:00 in India.
        var done = new DateTime(2026, 3, 4, 4, 30, 0, DateTimeKind.Utc);

        var task = new PmTask
        {
            PmScheduleId = schedule.Id, EquipmentId = machine.Id, DueDate = new DateOnly(2026, 3, 3),
            Status = PmTaskStatus.Completed, CompletedAtUtc = done, CompletedByUserId = signer.Id,
            ChecklistTemplateVersionId = version.Id,
        };
        db.PmTasks.Add(task);
        await db.SaveChangesAsync();
        _taskId = task.Id;

        db.PmCompletions.Add(new PmCompletion
        {
            PmTaskId = task.Id,
            ChecklistTemplateVersionId = version.Id,
            Answers = new Dictionary<string, ChecklistAnswer>
            {
                ["alarm"] = new() { Value = "fail", Note = "Did not sound" },
                ["flow"] = new() { Value = "7.2", OutOfRange = true },
            },
            CompletedByUserId = signer.Id,
            CompletedAtUtc = done,
            PerformedAtUtc = done,
            SignedByName = "Ward Technician",
            Signature = Signature,
            SignatureFormat = "png",
            Notes = "Second visit needed",
        });

        var order = new WorkOrder
        {
            EquipmentId = machine.Id,
            Status = WorkOrderStatus.Closed,
            Priority = WorkOrderPriority.High,
            FaultDescription = "Alarm sounds with no cause",
            ReportedByUserId = signer.Id,
            ReportedAtUtc = done,
            ResolutionNotes = "Replaced flow sensor",
            ResolvedByUserId = signer.Id,
            ResolvedAtUtc = done.AddHours(6),
            ClosedAtUtc = done.AddHours(7),
            OutOfServiceAtUtc = done,
            BackInServiceAtUtc = done.AddHours(5.5),
        };
        db.WorkOrders.Add(order);
        await db.SaveChangesAsync();
        // The number is given by the database when the row is inserted.
        _workOrderNumber = await db.WorkOrders.AsNoTracking().Where(w => w.Id == order.Id).Select(w => w.Number).SingleAsync();

        db.WorkOrderNotes.Add(new WorkOrderNote
        {
            WorkOrderId = order.Id, Body = "Sensor ordered", AuthorUserId = signer.Id, CreatedAtUtc = done.AddHours(1),
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _admin?.Dispose();
        _employee?.Dispose();
        _factory?.Dispose();
    }

    // ---- Reading the zip ------------------------------------------------------

    private sealed class Export(byte[] bytes)
    {
        private readonly ZipArchive _zip = new(new MemoryStream(bytes), ZipArchiveMode.Read);

        public IReadOnlyList<string> Names => _zip.Entries.Select(e => e.FullName).ToList();

        public string Text(string name)
        {
            using var reader = new StreamReader(_zip.GetEntry(name)!.Open(), new UTF8Encoding(true));
            return reader.ReadToEnd();
        }

        public byte[] Bytes(string name)
        {
            using var stream = _zip.GetEntry(name)!.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        public List<string[]> Csv(string name) => ParseCsv(Text(name).TrimStart('﻿'));
    }

    private async Task<Export> ExportAsync(string query = "")
    {
        var response = await _admin.GetAsync($"/api/admin/export{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new Export(await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>A small RFC 4180 reader: quotes, doubled quotes, and newlines inside a cell.</summary>
    private static List<string[]> ParseCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r') { }
            else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add([.. row]); row.Clear(); }
            else cell.Append(c);
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add([.. row]);
        }

        return rows;
    }

    private static string[] Find(List<string[]> csv, string column, string value)
    {
        var at = Array.IndexOf(csv[0], column);
        Assert.True(at >= 0, $"no column {column}");
        return csv.Skip(1).Single(r => r[at] == value);
    }

    private static string Cell(List<string[]> csv, string[] row, string column) => row[Array.IndexOf(csv[0], column)];

    // ---- Who may ask ------------------------------------------------------------

    [Fact]
    public async Task An_employee_cannot_export_the_hospitals_records()
    {
        var response = await _employee.GetAsync("/api/admin/export");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Nobody_signed_out_can_export()
    {
        using var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/export")).StatusCode);
    }

    // ---- What is in it ------------------------------------------------------------

    [Fact]
    public async Task It_is_a_zip_with_a_sensible_name()
    {
        var response = await _admin.GetAsync("/api/admin/export");

        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Matches(@"^hospitalpm-export-\d{8}-\d{4}-IST\.zip$", response.Content.Headers.ContentDisposition?.FileName);
    }

    [Fact]
    public async Task Every_kind_of_record_has_a_file_and_the_manifest_comes_last()
    {
        var export = await ExportAsync();

        foreach (var name in new[]
                 {
                     "README.txt", "locations.csv", "equipment.csv", "checklists.csv", "pm_schedules.csv",
                     "pm_tasks.csv", "pm_completions.csv", "pm_answers.csv", "work_orders.csv",
                     "work_order_notes.csv", "staff.csv", "manifest.json",
                 })
        {
            Assert.Contains(name, export.Names);
        }

        // Written last, so that its presence says the rest arrived.
        Assert.Equal("manifest.json", export.Names[^1]);
    }

    [Fact]
    public async Task The_manifest_row_counts_match_what_is_in_the_files()
    {
        var export = await ExportAsync();
        var manifest = JsonDocument.Parse(export.Text("manifest.json")).RootElement;

        foreach (var file in manifest.GetProperty("files").EnumerateArray())
        {
            var name = file.GetProperty("file").GetString()!;
            var rows = file.GetProperty("rows").GetInt32();

            if (name.EndsWith(".csv", StringComparison.Ordinal))
            {
                Assert.Equal(rows, export.Csv(name).Count - 1);
            }
            else if (name == "checklists/")
            {
                Assert.Equal(rows, export.Names.Count(n => n.StartsWith("checklists/", StringComparison.Ordinal)));
            }
        }

        Assert.False(manifest.GetProperty("includesSignatures").GetBoolean());
        Assert.Equal("IST", manifest.GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task The_machine_is_there_and_awkward_text_survives_a_round_trip()
    {
        var csv = (await ExportAsync()).Csv("equipment.csv");
        var row = Find(csv, "Asset Tag", _tag);

        Assert.Equal("Hamilton, Inc.", Cell(csv, row, "Manufacturer"));
        Assert.Equal("InService", Cell(csv, row, "Status"));
        Assert.Equal($"EXR-{_suffix}", Cell(csv, row, "Location"));

        // Starts with =, so a spreadsheet would run it: kept as text.
        Assert.Equal("'=1+1, said \"check the hose\"", Cell(csv, row, "Notes"));
    }

    [Fact]
    public async Task The_first_columns_of_the_register_match_the_import_template()
    {
        var export = await ExportAsync();
        var exported = export.Csv("equipment.csv")[0].Take(11).ToArray();

        var template = await _admin.GetAsync("/api/equipment/import/template");
        template.EnsureSuccessStatusCode();
        using var workbook = new XLWorkbook(await template.Content.ReadAsStreamAsync());
        var headerRow = workbook.Worksheets.SelectMany(w => w.RowsUsed()).First(r => r.Cell(1).GetString() == "Asset Tag");
        var expected = headerRow.CellsUsed().Select(c => c.GetString()).Take(11).ToArray();

        Assert.Equal(expected, exported);
    }

    [Fact]
    public async Task A_completed_PM_carries_who_when_and_what_was_found_with_times_in_IST()
    {
        var export = await ExportAsync();

        var completions = export.Csv("pm_completions.csv");
        var row = Find(completions, "Task Id", _taskId.ToString());

        Assert.Equal(_tag, Cell(completions, row, "Asset Tag"));
        Assert.Equal("Ward Technician", Cell(completions, row, "Performed By"));
        Assert.Equal("2026-03-04T10:00:00+05:30", Cell(completions, row, "Performed"));
        Assert.Equal("1", Cell(completions, row, "Out Of Range"));
        Assert.Equal("1", Cell(completions, row, "Failed Checks"));
        Assert.Equal("1", Cell(completions, row, "Checklist Version"));

        var tasks = export.Csv("pm_tasks.csv");
        var task = Find(tasks, "Task Id", _taskId.ToString());
        Assert.Equal("2026-03-03", Cell(tasks, task, "Due Date"));
        Assert.Equal("Completed", Cell(tasks, task, "Status"));
    }

    [Fact]
    public async Task Every_answer_is_a_row_with_the_question_it_answered()
    {
        var answers = (await ExportAsync()).Csv("pm_answers.csv");
        var mine = answers.Skip(1).Where(r => r[0] == _taskId.ToString()).ToList();

        Assert.Equal(2, mine.Count);

        var alarm = mine.Single(r => r[Array.IndexOf(answers[0], "Check")] == "Alarm test");
        Assert.Equal("Safety", alarm[Array.IndexOf(answers[0], "Section")]);
        Assert.Equal("fail", alarm[Array.IndexOf(answers[0], "Answer")]);
        Assert.Equal("Yes", alarm[Array.IndexOf(answers[0], "Failed")]);
        Assert.Equal("Did not sound", alarm[Array.IndexOf(answers[0], "Note")]);

        var flow = mine.Single(r => r[Array.IndexOf(answers[0], "Check")] == "Flow rate");
        Assert.Equal("7.2", flow[Array.IndexOf(answers[0], "Answer")]);
        Assert.Equal("L/min", flow[Array.IndexOf(answers[0], "Unit")]);
        Assert.Equal("Yes", flow[Array.IndexOf(answers[0], "Out Of Range")]);
    }

    [Fact]
    public async Task A_fault_carries_its_downtime_and_its_notes()
    {
        var export = await ExportAsync();

        var orders = export.Csv("work_orders.csv");
        var order = Find(orders, "Number", _workOrderNumber);
        Assert.Equal("5.5", Cell(orders, order, "Hours Out Of Service"));
        Assert.Equal("Closed", Cell(orders, order, "Status"));
        Assert.Equal("Replaced flow sensor", Cell(orders, order, "Resolution"));

        var notes = export.Csv("work_order_notes.csv");
        Assert.Equal("Sensor ordered", Cell(notes, Find(notes, "Work Order", _workOrderNumber), "Note"));
    }

    [Fact]
    public async Task Every_version_of_a_checklist_is_kept_whole()
    {
        var export = await ExportAsync();

        var json = JsonDocument.Parse(export.Text($"checklists/ex-{_suffix}-v1.json")).RootElement;
        var items = json.GetProperty("definition").GetProperty("sections")[0].GetProperty("items");

        Assert.Equal("Export PM", json.GetProperty("name").GetString());
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("L/min", items[1].GetProperty("unit").GetString());
    }

    // ---- What is not in it --------------------------------------------------------

    [Fact]
    public async Task Nothing_that_signs_anyone_in_leaves()
    {
        string hash;
        await using (var db = fixture.CreateContext())
        {
            hash = await db.Users.Where(u => u.UserName == $"ex-emp-{_suffix}").Select(u => u.PasswordHash!).SingleAsync();
        }

        var export = await ExportAsync();

        foreach (var name in export.Names.Where(n => !n.EndsWith('/')))
        {
            var text = Encoding.UTF8.GetString(export.Bytes(name));
            Assert.DoesNotContain(hash, text, StringComparison.Ordinal);
            Assert.DoesNotContain("PasswordHash", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("SecurityStamp", text, StringComparison.OrdinalIgnoreCase);
        }

        var staff = export.Csv("staff.csv");
        var person = Find(staff, "User Name", $"ex-emp-{_suffix}");
        Assert.Equal("BmeEngineer", Cell(staff, person, "Role"));
        Assert.Equal("Yes", Cell(staff, person, "Active"));
    }

    [Fact]
    public async Task Signatures_are_left_out_unless_asked_for()
    {
        var plain = await ExportAsync();
        Assert.DoesNotContain(plain.Names, n => n.StartsWith("signatures/", StringComparison.Ordinal));
        Assert.Contains("Signature images", plain.Text("README.txt"), StringComparison.Ordinal);

        var withSignatures = await ExportAsync("?signatures=true");
        Assert.Equal(Signature, withSignatures.Bytes($"signatures/task-{_taskId}.png"));
        Assert.True(
            JsonDocument.Parse(withSignatures.Text("manifest.json")).RootElement.GetProperty("includesSignatures").GetBoolean());
    }

    [Fact]
    public async Task The_readme_says_what_each_file_is_and_how_to_tell_it_is_complete()
    {
        var readme = (await ExportAsync()).Text("README.txt");

        Assert.Contains("equipment.csv", readme, StringComparison.Ordinal);
        Assert.Contains("pm_answers.csv", readme, StringComparison.Ordinal);
        Assert.Contains("+05:30", readme, StringComparison.Ordinal);
        Assert.Contains("manifest.json is written last", readme, StringComparison.Ordinal);
        Assert.Contains("audit trail", readme, StringComparison.OrdinalIgnoreCase);
    }
}
