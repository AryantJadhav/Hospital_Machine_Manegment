using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.Maintenance;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// A PM does not need a checklist.
///
/// This is a management system: the department wants to know that a PM was due, whether it was
/// done, by whom and when, and to keep the report. So a PM can be scheduled with no checklist, by
/// a repeating pattern or by dates picked by hand, and recorded as done with a date, a name, notes
/// and, if there is one, the report. It has no questions, no answers and no certificate.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PmWithoutChecklistTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "PmPlain2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private string _suffix = null!;
    private int _typeId;
    private int _roomId;

    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF");

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var room = new Location { Code = $"PN-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();
            _roomId = room.Id;
            _typeId = (await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator")).Id;
        }

        _admin = await SignedInAsync($"pn-adm-{_suffix}", Roles.Admin);
        _employee = await SignedInAsync($"pn-emp-{_suffix}", Roles.Employee);
    }

    private async Task<HttpClient> SignedInAsync(string userName, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = userName, FullName = userName, IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return client;
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

    private string Tag(string kind) => $"PN-{kind}-{_suffix}";

    private async Task<int> MachineAsync(string kind, bool contract = false)
    {
        await using var db = fixture.CreateContext();
        var machine = new Domain.Assets.Equipment
        {
            AssetTag = Tag(kind), EquipmentTypeId = _typeId, LocationId = _roomId,
            MaintenanceContractType = contract ? MaintenanceContractType.Amc : null,
            MaintenanceVendor = contract ? "Philips Healthcare" : null,
            MaintenanceStartDate = contract ? new DateOnly(2026, 1, 1) : null,
            MaintenanceEndDate = contract ? new DateOnly(2026, 12, 31) : null,
        };
        db.Equipment.Add(machine);
        await db.SaveChangesAsync();
        return machine.Id;
    }

    private async Task<List<PmTask>> TasksAsync(int machineId)
    {
        await using var db = fixture.CreateContext();
        return await db.PmTasks.AsNoTracking().Where(t => t.EquipmentId == machineId).OrderBy(t => t.DueDate).ToListAsync();
    }

    private object Schedule(int machine, int frequency, int by = 10, int? interval = null) => new
    {
        equipmentId = machine, frequency, intervalDays = interval ?? 0,
        anchorDate = Iso(Today().AddDays(20)), graceDays = 7, performedBy = by,
    };

    private object Dates(int machine, IEnumerable<DateOnly> dates, int by = 10) => new
    {
        equipmentId = machine, dates = dates.Select(Iso).ToArray(), graceDays = 7, performedBy = by,
    };

    private static MultipartFormDataContent Form(string? performedOn, string? doneBy, string? notes, params (string Name, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();
        if (performedOn is not null) form.Add(new StringContent(performedOn), "performedOn");
        if (doneBy is not null) form.Add(new StringContent(doneBy), "doneBy");
        if (notes is not null) form.Add(new StringContent(notes), "notes");
        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "files", name);
        }

        return form;
    }

    // ------------------------------------------------------------- scheduling with no checklist

    [Fact]
    public async Task A_machine_can_be_added_with_a_repeating_PM_and_no_checklist()
    {
        var created = await _admin.PostAsJsonAsync("/api/equipment", new
        {
            assetTag = Tag("A"), equipmentTypeId = _typeId, locationId = _roomId,
            pm = new { frequency = 20, firstDueDate = Iso(Today().AddDays(10)), graceDays = 7 },
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        await using var db = fixture.CreateContext();
        var schedule = await db.PmSchedules.SingleAsync(s => s.EquipmentId == id);
        Assert.Null(schedule.ChecklistTemplateId);
        Assert.Equal(PmFrequency.Quarterly, schedule.Frequency);

        // Its first PM is on the work list straight away, called plain "PM".
        var task = Assert.Single(await TasksAsync(id));
        Assert.Equal(Today().AddDays(10), task.DueDate);

        var list = await _employee.GetFromJsonAsync<JsonElement>($"/api/pm/tasks?equipmentId={id}");
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal("PM", item.GetProperty("checklistName").GetString());
        Assert.False(item.GetProperty("hasChecklist").GetBoolean());
    }

    [Fact]
    public async Task A_machine_can_be_added_with_dates_picked_by_hand_and_no_checklist()
    {
        var picked = new[] { Today().AddDays(10), Today().AddDays(70) };

        var created = await _admin.PostAsJsonAsync("/api/equipment", new
        {
            assetTag = Tag("B"), equipmentTypeId = _typeId, locationId = _roomId,
            pm = new { frequency = 95, firstDueDate = Iso(picked[0]), graceDays = 7, dates = picked.Select(Iso).ToArray() },
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(picked, (await TasksAsync(id)).Select(t => t.DueDate).ToArray());
    }

    [Fact]
    public async Task A_checklist_that_is_given_is_still_checked_but_one_is_not_needed()
    {
        var wrong = await _admin.PostAsJsonAsync("/api/equipment", new
        {
            assetTag = Tag("C"), equipmentTypeId = _typeId, locationId = _roomId,
            pm = new { checklistTemplateId = 2_000_000_000, frequency = 20, firstDueDate = Iso(Today()), graceDays = 7 },
        });

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
    }

    [Fact]
    public async Task An_existing_machine_gets_a_repeating_PM_with_no_checklist_once_for_each_doer()
    {
        var machine = await MachineAsync("D", contract: true);

        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync("/api/pm/schedules", Schedule(machine, 20))).StatusCode);

        // The same machine, the same doer, twice would make duplicate work.
        Assert.Equal(HttpStatusCode.Conflict, (await _admin.PostAsJsonAsync("/api/pm/schedules", Schedule(machine, 10))).StatusCode);

        // The vendor's own PM on the same machine is a different one.
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync("/api/pm/schedules", Schedule(machine, 30, by: 20))).StatusCode);

        await using var db = fixture.CreateContext();
        Assert.Equal(2, await db.PmSchedules.CountAsync(s => s.EquipmentId == machine && s.ChecklistTemplateId == null));
    }

    [Fact]
    public async Task Dates_picked_by_hand_with_no_checklist_go_on_one_schedule_for_each_doer()
    {
        var machine = await MachineAsync("E", contract: true);

        var one = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, [Today().AddDays(10)]));
        var two = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, [Today().AddDays(50)]));
        var vendor = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, [Today().AddDays(30)], by: 20));

        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        Assert.Equal(HttpStatusCode.OK, two.StatusCode);
        Assert.Equal(HttpStatusCode.OK, vendor.StatusCode);

        var first = (await one.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scheduleId").GetInt32();
        var second = (await two.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scheduleId").GetInt32();
        Assert.Equal(first, second);
        Assert.NotEqual(first, (await vendor.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("scheduleId").GetInt32());
        Assert.Equal(3, (await TasksAsync(machine)).Count);
    }

    [Fact]
    public async Task A_repeating_PM_with_no_checklist_keeps_its_pattern_against_hand_picked_dates()
    {
        var machine = await MachineAsync("F");
        await _admin.PostAsJsonAsync("/api/pm/schedules", Schedule(machine, 20));

        var res = await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, [Today().AddDays(5)]));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task The_database_allows_one_checklist_free_schedule_for_each_doer_and_no_more()
    {
        var machine = await MachineAsync("G", contract: true);
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();

        async Task Insert(int by)
        {
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO pm_schedule (equipment_id, checklist_template_id, frequency, interval_days, anchor_date, grace_days, performed_by) " +
                "VALUES (@m, NULL, 20, 0, '2027-01-01', 7, @by)", conn);
            cmd.Parameters.AddWithValue("m", machine);
            cmd.Parameters.AddWithValue("by", by);
            await cmd.ExecuteNonQueryAsync();
        }

        await Insert(10);
        await Insert(20);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => Insert(10));
        Assert.Equal("ux_pm_schedule_equipment_plain", ex.ConstraintName);
    }

    // ------------------------------------------------------------- recording it as done

    private async Task<int> PlainTaskAsync(string kind, bool contract = false, int by = 10, int dueInDays = 0)
    {
        var machine = await MachineAsync(kind, contract);
        await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, [Today().AddDays(dueInDays)], by));
        return (await TasksAsync(machine)).Single().Id;
    }

    [Fact]
    public async Task A_PM_with_no_checklist_is_recorded_as_done_with_a_date_a_name_notes_and_the_report()
    {
        var task = await PlainTaskAsync("H");

        var page = await _employee.GetFromJsonAsync<JsonElement>($"/api/pm/tasks/{task}/record");
        Assert.True(page.GetProperty("simple").GetBoolean());
        Assert.Equal((int)PmPerformedBy.InHouse, page.GetProperty("performedBy").GetInt32());
        Assert.Equal(JsonValueKind.Null, page.GetProperty("vendorName").ValueKind);

        var res = await _employee.PostAsync(
            $"/api/pm/tasks/{task}/done",
            Form(Iso(Today().AddDays(-1)), "A. Kulkarni", "Cleaned and tested", ("report.pdf", PdfBytes)));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        await using var db = fixture.CreateContext();
        var pm = await db.PmTasks.SingleAsync(t => t.Id == task);
        Assert.Equal(PmTaskStatus.Completed, pm.Status);
        Assert.Null(pm.ChecklistTemplateVersionId);

        var completion = await db.PmCompletions.SingleAsync(c => c.PmTaskId == task);
        Assert.Null(completion.ChecklistTemplateVersionId);
        Assert.Equal(PmPerformedBy.InHouse, completion.PerformedBy);
        Assert.Null(completion.VendorName);
        Assert.Equal("A. Kulkarni", completion.SignedByName);
        Assert.Equal("Cleaned and tested", completion.Notes);
        Assert.Empty(completion.Answers);

        var file = Assert.Single(await db.PmTaskAttachments.Where(a => a.PmTaskId == task).ToListAsync());
        Assert.Equal("report.pdf", file.FileName);

        var after = await _employee.GetFromJsonAsync<JsonElement>($"/api/pm/tasks/{task}/record");
        Assert.Equal("A. Kulkarni", after.GetProperty("completion").GetProperty("engineerName").GetString());
        Assert.Equal(1, after.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task No_report_is_needed_and_one_can_be_added_later()
    {
        var task = await PlainTaskAsync("I");

        var done = await _employee.PostAsync($"/api/pm/tasks/{task}/done", Form(Iso(Today()), null, null));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);

        var later = await _employee.PostAsync(
            $"/api/pm/tasks/{task}/attachments", Form(null, null, null, ("late.pdf", PdfBytes)));
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }

    [Fact]
    public async Task The_vendor_is_recorded_the_same_way_and_named_from_the_contract()
    {
        var task = await PlainTaskAsync("J", contract: true, by: 20);

        var res = await _employee.PostAsync(
            $"/api/pm/tasks/{task}/done", Form(Iso(Today()), "R. Sharma", null, ("r.pdf", PdfBytes)));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await using var db = fixture.CreateContext();
        var completion = await db.PmCompletions.SingleAsync(c => c.PmTaskId == task);
        Assert.Equal(PmPerformedBy.Vendor, completion.PerformedBy);
        Assert.Equal("Philips Healthcare", completion.VendorName);
        Assert.Null(completion.ChecklistTemplateVersionId);
    }

    [Fact]
    public async Task A_PM_with_no_checklist_has_no_form_to_fill_and_no_certificate()
    {
        var task = await PlainTaskAsync("K");

        var form = await _employee.GetAsync($"/api/pm/tasks/{task}/form");
        Assert.Equal(HttpStatusCode.Conflict, form.StatusCode);

        var complete = await _employee.PostAsJsonAsync($"/api/pm/tasks/{task}/complete", new
        {
            checklistTemplateVersionId = 1, answers = new Dictionary<string, object>(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, complete.StatusCode);

        await _employee.PostAsync($"/api/pm/tasks/{task}/done", Form(Iso(Today()), "A. Kulkarni", null));
        var certificate = await _employee.GetAsync($"/api/reports/pm/{task}/certificate.pdf");
        Assert.Equal(HttpStatusCode.Conflict, certificate.StatusCode);

        // And the finished PM can still be read back.
        var completion = await _employee.GetAsync($"/api/pm/tasks/{task}/completion");
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await completion.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionNo").ValueKind);
    }

    [Fact]
    public async Task The_machines_history_shows_a_recorded_PM_without_a_certificate()
    {
        var machine = await MachineAsync("L");
        await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, [Today()]));
        var task = (await TasksAsync(machine)).Single().Id;
        await _employee.PostAsync($"/api/pm/tasks/{task}/done", Form(Iso(Today()), "A. Kulkarni", null));

        var history = await _employee.GetFromJsonAsync<JsonElement>($"/api/equipment/{machine}/history");
        var pm = history.GetProperty("completedPm").EnumerateArray().Single();

        Assert.Equal("PM", pm.GetProperty("checklistName").GetString());
        Assert.False(pm.GetProperty("hasCertificate").GetBoolean());
        Assert.Equal((int)PmPerformedBy.InHouse, pm.GetProperty("performedBy").GetInt32());
    }

    [Fact]
    public async Task Reminders_and_the_reports_cope_with_PMs_that_have_no_checklist()
    {
        var machine = await MachineAsync("M");
        await _admin.PostAsJsonAsync("/api/pm/schedules/dates", Dates(machine, [Today().AddDays(2), Today().AddDays(-3)]));
        var done = (await TasksAsync(machine)).First().Id;
        await _employee.PostAsync($"/api/pm/tasks/{done}/done", Form(Iso(Today()), "A. Kulkarni", null));

        var reminders = await _employee.GetFromJsonAsync<JsonElement>("/api/pm/reminders");
        Assert.Contains(
            reminders.GetProperty("upcoming").EnumerateArray(),
            i => i.GetProperty("assetTag").GetString() == Tag("M") && i.GetProperty("checklistName").GetString() == "PM");

        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync("/api/reports/pm-compliance")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _admin.GetAsync("/api/reports/pm-compliance/report.csv")).StatusCode);

        var export = await _admin.GetAsync("/api/admin/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        using var zip = new ZipArchive(new MemoryStream(await export.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("pm_completions.csv")!.Open(), Encoding.UTF8);
        Assert.Contains("Our team", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_PM_that_has_a_checklist_is_still_filled_in_and_not_marked_done()
    {
        int task;
        await using (var db = fixture.CreateContext())
        {
            var machine = new Domain.Assets.Equipment { AssetTag = Tag("N"), EquipmentTypeId = _typeId, LocationId = _roomId };
            var template = new Domain.Checklists.ChecklistTemplate { EquipmentTypeId = _typeId, Code = $"pn-{_suffix}", Name = "Has questions" };
            db.AddRange(machine, template);
            await db.SaveChangesAsync();
            var schedule = new PmSchedule
            {
                EquipmentId = machine.Id, ChecklistTemplateId = template.Id, Frequency = PmFrequency.Yearly,
                AnchorDate = Today().AddYears(3), GraceDays = 7,
            };
            db.PmSchedules.Add(schedule);
            await db.SaveChangesAsync();
            var pm = new PmTask { PmScheduleId = schedule.Id, EquipmentId = machine.Id, DueDate = Today(), Status = PmTaskStatus.Due };
            db.PmTasks.Add(pm);
            await db.SaveChangesAsync();
            task = pm.Id;
        }

        var page = await _employee.GetFromJsonAsync<JsonElement>($"/api/pm/tasks/{task}/record");
        Assert.False(page.GetProperty("simple").GetBoolean());

        var res = await _employee.PostAsync($"/api/pm/tasks/{task}/done", Form(Iso(Today()), "A. Kulkarni", null));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
