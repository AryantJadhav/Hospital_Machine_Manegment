using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HospitalPm.Api.Maintenance;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Checklists;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.Maintenance;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// PMs done by the maintenance contract vendor, and the report they hand over.
///
/// Where a machine is under an AMC or a CMC its PM can be the vendor's. Our staff record
/// that it was done, on the vendor's behalf, and save the report, a PDF or a photo, against
/// the PM. The report is kept in the database so a backup and a restore carry it.
///
/// What a file really is comes from its bytes and not its name: a page called report.pdf
/// would otherwise run in the application's own address when someone opened it.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class PmVendorReportTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "PmVendor2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private string _suffix = null!;
    private int _machineId;
    private int _vendorTaskId;
    private int _vendorScheduleId;
    private int _inHouseTaskId;
    private int _templateId;

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(5.5));

    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\n%%EOF");
    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 2, 3];
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] WebpBytes = Encoding.ASCII.GetBytes("RIFF\0\0\0\0WEBPVP8 ");
    private static readonly byte[] HtmlBytes = Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>");

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var room = new Location { Code = $"PV-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();

            var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
            var machine = new Domain.Assets.Equipment
            {
                AssetTag = $"PV-{_suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
                MaintenanceContractType = MaintenanceContractType.Amc, MaintenanceVendor = "Philips Healthcare",
                MaintenanceContractNumber = "AMC-42",
                MaintenanceStartDate = new DateOnly(2026, 1, 1), MaintenanceEndDate = new DateOnly(2026, 12, 31),
            };
            var vendorTemplate = new ChecklistTemplate { EquipmentTypeId = type.Id, Code = $"pv-{_suffix}", Name = "Vendor PM" };
            var houseTemplate = new ChecklistTemplate { EquipmentTypeId = type.Id, Code = $"pv-h-{_suffix}", Name = "House PM" };
            db.AddRange(machine, vendorTemplate, houseTemplate);
            await db.SaveChangesAsync();
            _machineId = machine.Id;
            _templateId = vendorTemplate.Id;

            db.ChecklistTemplateVersions.Add(new ChecklistTemplateVersion
            {
                ChecklistTemplateId = vendorTemplate.Id, VersionNo = 1,
                Status = ChecklistVersionStatus.Published, PublishedAtUtc = DateTime.UtcNow,
                Definition = new ChecklistDefinition { Sections = [] },
            });
            db.ChecklistTemplateVersions.Add(new ChecklistTemplateVersion
            {
                ChecklistTemplateId = houseTemplate.Id, VersionNo = 1,
                Status = ChecklistVersionStatus.Published, PublishedAtUtc = DateTime.UtcNow,
                Definition = new ChecklistDefinition { Sections = [] },
            });

            // Far in the future, so nothing else is generated for this machine.
            var vendorSchedule = new PmSchedule
            {
                EquipmentId = machine.Id, ChecklistTemplateId = vendorTemplate.Id, Frequency = PmFrequency.Yearly,
                AnchorDate = Today().AddYears(3), GraceDays = 7, PerformedBy = PmPerformedBy.Vendor,
            };
            var houseSchedule = new PmSchedule
            {
                EquipmentId = machine.Id, ChecklistTemplateId = houseTemplate.Id, Frequency = PmFrequency.Yearly,
                AnchorDate = Today().AddYears(3), GraceDays = 7,
            };
            db.PmSchedules.AddRange(vendorSchedule, houseSchedule);
            await db.SaveChangesAsync();
            _vendorScheduleId = vendorSchedule.Id;

            var vendorTask = new PmTask
            {
                PmScheduleId = vendorSchedule.Id, EquipmentId = machine.Id, DueDate = Today(), Status = PmTaskStatus.Due,
            };
            var houseTask = new PmTask
            {
                PmScheduleId = houseSchedule.Id, EquipmentId = machine.Id, DueDate = Today(), Status = PmTaskStatus.Due,
            };
            db.PmTasks.AddRange(vendorTask, houseTask);
            await db.SaveChangesAsync();
            _vendorTaskId = vendorTask.Id;
            _inHouseTaskId = houseTask.Id;
        }

        _admin = await SignedInAsync($"pv-adm-{_suffix}", Roles.Admin);
        _employee = await SignedInAsync($"pv-emp-{_suffix}", Roles.Employee);
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

    private static MultipartFormDataContent Form(
        string? performedOn, string? engineer = null, string? notes = null, params (string Name, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();
        if (performedOn is not null) form.Add(new StringContent(performedOn), "performedOn");
        if (engineer is not null) form.Add(new StringContent(engineer), "doneBy");
        if (notes is not null) form.Add(new StringContent(notes), "notes");
        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "files", name);
        }

        return form;
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");

    private Task<HttpResponseMessage> CompleteAsync(
        HttpClient client, int task, string? performedOn, string? engineer = "R. Sharma", string? notes = null,
        params (string Name, byte[] Bytes)[] files)
        => client.PostAsync($"/api/pm/tasks/{task}/done", Form(performedOn, engineer, notes, files));

    private async Task<JsonElement> VendorAsync(int task) =>
        await _employee.GetFromJsonAsync<JsonElement>($"/api/pm/tasks/{task}/record");

    private async Task<PmTask> TaskAsync(int id)
    {
        await using var db = fixture.CreateContext();
        return await db.PmTasks.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    [Fact]
    public async Task A_vendors_PM_says_who_the_vendor_is_and_that_it_can_be_recorded()
    {
        var body = await VendorAsync(_vendorTaskId);

        Assert.True(body.GetProperty("simple").GetBoolean());
        Assert.Equal("Philips Healthcare", body.GetProperty("vendorName").GetString());
        Assert.Equal("AMC-42", body.GetProperty("contractNumber").GetString());
        Assert.Equal((int)MaintenanceContractType.Amc, body.GetProperty("contractType").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("completion").ValueKind);
        Assert.Equal(0, body.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task A_PM_that_is_our_own_says_so()
    {
        var body = await VendorAsync(_inHouseTaskId);

        Assert.False(body.GetProperty("simple").GetBoolean());
    }

    [Fact]
    public async Task A_PM_that_does_not_exist_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _employee.GetAsync("/api/pm/tasks/2000000000/record")).StatusCode);
    }

    [Fact]
    public async Task Recording_the_vendors_PM_with_a_PDF_saves_the_report_and_closes_the_PM()
    {
        var res = await CompleteAsync(
            _employee, _vendorTaskId, Iso(Today().AddDays(-1)), "R. Sharma", "Filters replaced",
            ("service report.pdf", PdfBytes));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("filesSaved").GetInt32());

        var task = await TaskAsync(_vendorTaskId);
        Assert.Equal(PmTaskStatus.Completed, task.Status);

        await using var db = fixture.CreateContext();
        var completion = await db.PmCompletions.SingleAsync(c => c.PmTaskId == _vendorTaskId);
        Assert.Equal(PmPerformedBy.Vendor, completion.PerformedBy);
        // The vendor as it was when this was recorded.
        Assert.Equal("Philips Healthcare", completion.VendorName);
        Assert.Equal("R. Sharma", completion.SignedByName);
        Assert.Equal("Filters replaced", completion.Notes);
        Assert.Empty(completion.Answers);
        // Midday at the hospital on the day given, whatever time zone reads it.
        Assert.Equal(Today().AddDays(-1), DateOnly.FromDateTime(completion.PerformedAtUtc!.Value.AddHours(5.5)));

        var file = await db.PmTaskAttachments.Include(a => a.Data).SingleAsync(a => a.PmTaskId == _vendorTaskId);
        Assert.Equal("service report.pdf", file.FileName);
        Assert.Equal("application/pdf", file.ContentType);
        Assert.Equal(PdfBytes.Length, file.SizeBytes);
        Assert.Equal(PdfBytes, file.Data!.Data);
    }

    [Fact]
    public async Task Photos_of_the_report_are_accepted_too_and_several_at_once()
    {
        var res = await CompleteAsync(
            _employee, _vendorTaskId, Iso(Today()), "R. Sharma", null,
            ("page1.jpeg", JpegBytes), ("page2.png", PngBytes), ("page3.webp", WebpBytes));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await VendorAsync(_vendorTaskId);
        var types = body.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("contentType").GetString()).ToList();
        Assert.Equal(new[] { "image/jpeg", "image/png", "image/webp" }, types);
    }

    [Fact]
    public async Task The_page_shows_the_completion_and_the_files_afterwards()
    {
        await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), "R. Sharma", "All good", ("r.pdf", PdfBytes));

        var body = await VendorAsync(_vendorTaskId);

        var completion = body.GetProperty("completion");
        Assert.Equal(Iso(Today()), completion.GetProperty("performedOn").GetString());
        Assert.Equal("R. Sharma", completion.GetProperty("engineerName").GetString());
        Assert.Equal("All good", completion.GetProperty("notes").GetString());
        Assert.Equal($"pv-emp-{_suffix}", completion.GetProperty("recordedBy").GetString());

        var file = Assert.Single(body.GetProperty("files").EnumerateArray());
        Assert.Equal("r.pdf", file.GetProperty("fileName").GetString());
        Assert.Equal($"pv-emp-{_suffix}", file.GetProperty("uploadedBy").GetString());
    }

    [Fact]
    public async Task A_report_can_be_added_later_because_it_often_arrives_after_the_visit()
    {
        var done = await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), "R. Sharma");
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Equal(0, (await VendorAsync(_vendorTaskId)).GetProperty("files").GetArrayLength());

        var later = await _employee.PostAsync(
            $"/api/pm/tasks/{_vendorTaskId}/attachments", Form(null, null, null, ("late.pdf", PdfBytes)));

        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
        Assert.Equal(1, (await VendorAsync(_vendorTaskId)).GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task Nothing_is_recorded_when_a_file_is_refused()
    {
        var res = await CompleteAsync(
            _employee, _vendorTaskId, Iso(Today()), "R. Sharma", null,
            ("good.pdf", PdfBytes), ("bad.pdf", HtmlBytes));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        // The PM is still open and nothing was saved: it is one step, not two.
        Assert.Equal(PmTaskStatus.Due, (await TaskAsync(_vendorTaskId)).Status);
        await using var db = fixture.CreateContext();
        Assert.False(await db.PmCompletions.AnyAsync(c => c.PmTaskId == _vendorTaskId));
        Assert.False(await db.PmTaskAttachments.AnyAsync(a => a.PmTaskId == _vendorTaskId));
    }

    [Fact]
    public async Task A_web_page_renamed_to_pdf_is_refused()
    {
        var res = await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), "R. Sharma", null, ("report.pdf", HtmlBytes));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("not a PDF or a photo", (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString());
    }

    [Fact]
    public async Task What_a_file_is_comes_from_its_bytes_and_the_name_is_made_to_match()
    {
        await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), "R. Sharma", null, ("..\\..\\my scan.txt", PdfBytes));

        var file = Assert.Single((await VendorAsync(_vendorTaskId)).GetProperty("files").EnumerateArray());

        // A PDF called .txt is kept and later served as a PDF, without the folders it came with.
        Assert.Equal("my scan.pdf", file.GetProperty("fileName").GetString());
        Assert.Equal("application/pdf", file.GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task An_empty_file_and_a_file_over_ten_megabytes_are_refused()
    {
        var empty = await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), null, null, ("e.pdf", []));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var big = new byte[ReportFile.MaxBytes + 1];
        Array.Copy(PdfBytes, big, PdfBytes.Length);
        var tooBig = await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), null, null, ("big.pdf", big));
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.StatusCode);
        Assert.Contains("larger than", (await tooBig.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        Assert.Equal(PmTaskStatus.Due, (await TaskAsync(_vendorTaskId)).Status);
    }

    [Fact]
    public async Task No_more_than_ten_files_go_against_one_PM()
    {
        var eleven = Enumerable.Range(1, 11).Select(n => ($"p{n}.pdf", PdfBytes)).ToArray();

        var res = await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), null, null, eleven);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        // Ten is fine, and then the eleventh, added later, is not.
        var ten = eleven.Take(10).ToArray();
        Assert.Equal(HttpStatusCode.OK, (await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), null, null, ten)).StatusCode);
        var more = await _employee.PostAsync(
            $"/api/pm/tasks/{_vendorTaskId}/attachments", Form(null, null, null, ("p11.pdf", PdfBytes)));
        Assert.Equal(HttpStatusCode.BadRequest, more.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    public async Task The_day_it_was_done_is_needed(string? day)
    {
        var res = await CompleteAsync(_employee, _vendorTaskId, day, "R. Sharma", null, ("r.pdf", PdfBytes));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task It_cannot_have_been_done_on_a_day_that_has_not_come()
    {
        var res = await CompleteAsync(_employee, _vendorTaskId, Iso(Today().AddDays(1)), "R. Sharma", null, ("r.pdf", PdfBytes));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(PmTaskStatus.Due, (await TaskAsync(_vendorTaskId)).Status);
    }

    [Fact]
    public async Task Our_own_PM_is_not_recorded_as_the_vendors()
    {
        var res = await CompleteAsync(_employee, _inHouseTaskId, Iso(Today()), "R. Sharma", null, ("r.pdf", PdfBytes));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(PmTaskStatus.Due, (await TaskAsync(_inHouseTaskId)).Status);
    }

    [Fact]
    public async Task A_PM_that_is_already_closed_is_not_recorded_twice()
    {
        await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), "R. Sharma", null, ("r.pdf", PdfBytes));

        var again = await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), "R. Sharma", null, ("r2.pdf", PdfBytes));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task With_no_contract_left_there_is_no_vendor_to_record()
    {
        await using (var db = fixture.CreateContext())
        {
            // Straight to the table: the machine's contract was removed after the PM was scheduled.
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE equipment SET maintenance_contract_type = NULL, maintenance_vendor = NULL,
                    maintenance_contract_number = NULL, maintenance_start_date = NULL,
                    maintenance_end_date = NULL, maintenance_cost = NULL
                WHERE id = {_machineId}");
        }

        var res = await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), "R. Sharma", null, ("r.pdf", PdfBytes));

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Files_are_added_only_to_a_PM_the_vendor_has_done()
    {
        var open = await _employee.PostAsync(
            $"/api/pm/tasks/{_vendorTaskId}/attachments", Form(null, null, null, ("r.pdf", PdfBytes)));
        Assert.Equal(HttpStatusCode.Conflict, open.StatusCode);

        var house = await _employee.PostAsync(
            $"/api/pm/tasks/{_inHouseTaskId}/attachments", Form(null, null, null, ("r.pdf", PdfBytes)));
        Assert.Equal(HttpStatusCode.Conflict, house.StatusCode);

        await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), "R. Sharma");
        var none = await _employee.PostAsync($"/api/pm/tasks/{_vendorTaskId}/attachments", Form(null));
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
    }

    [Fact]
    public async Task A_report_can_be_opened_by_everyone_signed_in_as_the_type_it_is()
    {
        await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), "R. Sharma", null, ("Report 1.pdf", PdfBytes));
        var id = (await VendorAsync(_vendorTaskId)).GetProperty("files")[0].GetProperty("id").GetInt32();

        foreach (var client in new[] { _employee, _admin })
        {
            var res = await client.GetAsync($"/api/pm/attachments/{id}");

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);
            Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal(PdfBytes, await res.Content.ReadAsByteArrayAsync());
        }

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/pm/attachments/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _employee.GetAsync("/api/pm/attachments/2000000000")).StatusCode);
    }

    [Fact]
    public async Task Only_an_administrator_can_remove_a_file_and_the_bytes_go_with_it()
    {
        await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), "R. Sharma", null, ("r.pdf", PdfBytes));
        var id = (await VendorAsync(_vendorTaskId)).GetProperty("files")[0].GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.Forbidden, (await _employee.DeleteAsync($"/api/pm/attachments/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync($"/api/pm/attachments/{id}")).StatusCode);

        await using var db = fixture.CreateContext();
        Assert.False(await db.PmTaskAttachments.AnyAsync(a => a.Id == id));
        Assert.False(await db.PmTaskAttachmentData.AnyAsync(d => d.AttachmentId == id));
        Assert.Equal(HttpStatusCode.NotFound, (await _admin.GetAsync($"/api/pm/attachments/{id}")).StatusCode);
    }

    [Fact]
    public async Task The_audit_log_records_a_file_being_added_and_removed_without_holding_the_file()
    {
        await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), "R. Sharma", null, ("r.pdf", PdfBytes));
        var id = (await VendorAsync(_vendorTaskId)).GetProperty("files")[0].GetProperty("id").GetInt32();
        await _admin.DeleteAsync($"/api/pm/attachments/{id}");

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT operation, length(new_data::text), length(old_data::text) FROM audit_log " +
            "WHERE table_name = 'pm_task_attachment' AND record_pk = @id ORDER BY id", conn);
        cmd.Parameters.AddWithValue("id", id.ToString());
        await using var reader = await cmd.ExecuteReaderAsync();

        var rows = new List<(string Op, int? New, int? Old)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetInt32(2)));
        }

        Assert.Equal(new[] { "INSERT", "DELETE" }, rows.Select(r => r.Op).ToArray());
        // A few hundred characters of description, not the file.
        Assert.All(rows, r => Assert.True((r.New ?? r.Old) < 1000));
    }

    [Fact]
    public async Task The_database_will_not_let_a_stored_file_be_changed()
    {
        await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), "R. Sharma", null, ("r.pdf", PdfBytes));
        var id = (await VendorAsync(_vendorTaskId)).GetProperty("files")[0].GetProperty("id").GetInt32();

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE pm_task_attachment SET file_name = 'other.pdf' WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);

        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task A_vendors_PM_has_no_certificate_of_ours()
    {
        await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), "R. Sharma", null, ("r.pdf", PdfBytes));

        var res = await _employee.GetAsync($"/api/reports/pm/{_vendorTaskId}/certificate.pdf");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Contains("no certificate", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task The_machines_history_says_the_vendor_did_it_and_how_many_files_there_are()
    {
        await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), "R. Sharma", null, ("a.pdf", PdfBytes), ("b.png", PngBytes));

        var history = await _employee.GetFromJsonAsync<JsonElement>($"/api/equipment/{_machineId}/history");
        var pm = history.GetProperty("completedPm").EnumerateArray().Single(p => p.GetProperty("id").GetInt32() == _vendorTaskId);

        Assert.Equal((int)PmPerformedBy.Vendor, pm.GetProperty("performedBy").GetInt32());
        Assert.Equal("Philips Healthcare", pm.GetProperty("vendorName").GetString());
        Assert.Equal(2, pm.GetProperty("reportFiles").GetInt32());
        Assert.False(pm.GetProperty("hasCertificate").GetBoolean());
    }

    [Fact]
    public async Task The_PM_list_says_which_PMs_are_the_vendors()
    {
        var list = await _employee.GetFromJsonAsync<JsonElement>($"/api/pm/tasks?equipmentId={_machineId}");

        var byId = list.GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("id").GetInt32(), i => i.GetProperty("performedBy").GetInt32());

        Assert.Equal((int)PmPerformedBy.Vendor, byId[_vendorTaskId]);
        Assert.Equal((int)PmPerformedBy.InHouse, byId[_inHouseTaskId]);
    }

    [Fact]
    public async Task The_export_carries_the_report_and_says_who_did_the_PM()
    {
        await CompleteAsync(_employee, _vendorTaskId, Iso(Today()), "R. Sharma", null, ("Final report.pdf", PdfBytes));

        var res = await _admin.GetAsync("/api/admin/export");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var zip = new ZipArchive(new MemoryStream(await res.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);

        var file = zip.Entries.Single(e => e.FullName.StartsWith($"pm_reports/task-{_vendorTaskId}-", StringComparison.Ordinal));
        Assert.EndsWith("Final report.pdf", file.FullName, StringComparison.Ordinal);
        using (var stream = file.Open())
        using (var copy = new MemoryStream())
        {
            await stream.CopyToAsync(copy);
            Assert.Equal(PdfBytes, copy.ToArray());
        }

        string Text(string name)
        {
            using var reader = new StreamReader(zip.GetEntry(name)!.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        Assert.Contains($"PV-{_suffix}".ToUpperInvariant(), Text("pm_reports.csv"), StringComparison.Ordinal);
        Assert.Contains("Final report.pdf", Text("pm_reports.csv"), StringComparison.Ordinal);
        Assert.Contains("Vendor", Text("pm_completions.csv"), StringComparison.Ordinal);
        Assert.Contains("Philips Healthcare", Text("pm_completions.csv"), StringComparison.Ordinal);
    }

    // ------------------------------------------------------ choosing the vendor when scheduling

    private object ScheduleBody(int machine, int performedBy) => new
    {
        equipmentId = machine,
        checklistTemplateId = _templateId,
        frequency = 40,
        intervalDays = 0,
        anchorDate = Iso(Today().AddYears(4)),
        graceDays = 7,
        performedBy,
    };

    [Fact]
    public async Task A_schedule_can_be_the_vendors_only_on_a_machine_with_a_contract()
    {
        int noContract;
        await using (var db = fixture.CreateContext())
        {
            var room = await db.Locations.FirstAsync(l => l.Code == $"PV-{_suffix}");
            var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
            var bare = new Domain.Assets.Equipment { AssetTag = $"PV-B-{_suffix}", EquipmentTypeId = type.Id, LocationId = room.Id };
            db.Equipment.Add(bare);
            await db.SaveChangesAsync();
            noContract = bare.Id;
        }

        var refused = await _admin.PostAsJsonAsync("/api/pm/schedules", ScheduleBody(noContract, 20));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("maintenance contract", (await refused.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString());

        // Our own team can do it on any machine.
        Assert.Equal(HttpStatusCode.Created, (await _admin.PostAsJsonAsync("/api/pm/schedules", ScheduleBody(noContract, 10))).StatusCode);
    }

    [Fact]
    public async Task A_new_machine_can_have_the_vendors_PM_when_it_is_given_a_contract_in_the_same_form()
    {
        await using var db = fixture.CreateContext();
        var room = await db.Locations.FirstAsync(l => l.Code == $"PV-{_suffix}");
        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");

        object Body(string tag, bool withContract) => new
        {
            assetTag = tag, equipmentTypeId = type.Id, locationId = room.Id,
            hasMaintenanceContract = withContract ? true : (bool?)null,
            maintenanceContractType = withContract ? 20 : (int?)null,
            maintenanceVendor = withContract ? "GE Healthcare" : null,
            maintenanceStartDate = withContract ? "2026-01-01" : null,
            maintenanceEndDate = withContract ? "2026-12-31" : null,
            pm = new
            {
                checklistTemplateId = _templateId, frequency = 20, firstDueDate = Iso(Today().AddDays(30)),
                graceDays = 7, performedBy = 20,
            },
        };

        var refused = await _admin.PostAsJsonAsync("/api/equipment", Body($"PV-N-{_suffix}", withContract: false));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.False(await db.Equipment.AnyAsync(e => e.AssetTag == $"PV-N-{_suffix}"));

        var created = await _admin.PostAsJsonAsync("/api/equipment", Body($"PV-Y-{_suffix}", withContract: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        Assert.Equal(PmPerformedBy.Vendor, (await db.PmSchedules.SingleAsync(s => s.EquipmentId == id)).PerformedBy);
    }

    [Fact]
    public async Task The_database_only_allows_the_two_kinds()
    {
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE pm_schedule SET performed_by = 30 WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", _vendorScheduleId);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("ck_pm_schedule_performed_by", ex.ConstraintName);
    }
}
