using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HospitalPm.Domain.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>A photo of the fault or the repair, attached to the ticket.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class WorkOrderPhotoTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "WorkOrderPhoto2026!";

    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 2, 3];
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\n%%EOF");

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private string _suffix = null!;
    private int _workOrderId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        int equipmentId;
        await using (var db = fixture.CreateContext())
        {
            var room = new Location { Code = $"WOPH-{_suffix}", Name = $"Ward {_suffix}", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();

            var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");

            var equipment = new Domain.Assets.Equipment
            {
                AssetTag = $"WOPH-{_suffix}".ToUpperInvariant(),
                EquipmentTypeId = type.Id,
                LocationId = room.Id,
            };
            db.Equipment.Add(equipment);
            await db.SaveChangesAsync();
            equipmentId = equipment.Id;
        }

        _admin = await SignInAsync("woph-admin", Domain.Identity.Roles.BmeHead);
        _employee = await SignInAsync("woph-emp", Domain.Identity.Roles.BmeEngineer);

        var created = await _employee.PostAsJsonAsync("/api/work-orders", new
        {
            equipmentId,
            faultDescription = "Casing cracked, needs a photo record",
            priority = 20,
        });
        created.EnsureSuccessStatusCode();
        _workOrderId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<HttpClient> SignInAsync(string prefix, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser
            {
                UserName = $"{prefix}-{_suffix}", FullName = $"{prefix} person", IsActive = true,
            };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            await users.AddToRoleAsync(user, role);
        }

        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { userName = $"{prefix}-{_suffix}", password = Password });
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

    private static MultipartFormDataContent Form(params (string Name, byte[] Bytes)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, bytes) in files)
        {
            var part = new ByteArrayContent(bytes);
            part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(part, "files", name);
        }

        return form;
    }

    [Fact]
    public async Task An_employee_can_attach_a_photo_and_it_is_readable_afterwards()
    {
        var res = await _employee.PostAsync($"/api/work-orders/{_workOrderId}/photos", Form(("crack.jpg", JpegBytes)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("photosSaved").GetInt32());

        var detail = await _employee.GetFromJsonAsync<JsonElement>($"/api/work-orders/{_workOrderId}");
        var photos = detail.GetProperty("photos").EnumerateArray().ToList();
        Assert.Single(photos);
        Assert.Equal("crack.jpg", photos[0].GetProperty("fileName").GetString());
        Assert.Equal("image/jpeg", photos[0].GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task Several_photo_types_are_accepted_at_once()
    {
        var res = await _employee.PostAsync(
            $"/api/work-orders/{_workOrderId}/photos", Form(("a.jpg", JpegBytes), ("b.png", PngBytes)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(2, (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("photosSaved").GetInt32());
    }

    [Fact]
    public async Task A_PDF_is_refused_a_work_order_only_takes_photos()
    {
        var res = await _employee.PostAsync($"/api/work-orders/{_workOrderId}/photos", Form(("report.pdf", PdfBytes)));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Something_that_is_not_a_real_photo_is_refused()
    {
        var res = await _employee.PostAsync(
            $"/api/work-orders/{_workOrderId}/photos", Form(("fake.jpg", Encoding.ASCII.GetBytes("not a photo"))));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task An_empty_file_is_refused()
    {
        var res = await _employee.PostAsync($"/api/work-orders/{_workOrderId}/photos", Form(("empty.jpg", [])));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_downloaded_photo_comes_back_byte_for_byte()
    {
        await _employee.PostAsync($"/api/work-orders/{_workOrderId}/photos", Form(("crack.jpg", JpegBytes)));
        var detail = await _employee.GetFromJsonAsync<JsonElement>($"/api/work-orders/{_workOrderId}");
        var photoId = detail.GetProperty("photos")[0].GetProperty("id").GetInt32();

        var download = await _employee.GetAsync($"/api/work-orders/photos/{photoId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("image/jpeg", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal(JpegBytes, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task An_employee_cannot_remove_a_photo_but_an_admin_can()
    {
        await _employee.PostAsync($"/api/work-orders/{_workOrderId}/photos", Form(("crack.jpg", JpegBytes)));
        var detail = await _employee.GetFromJsonAsync<JsonElement>($"/api/work-orders/{_workOrderId}");
        var photoId = detail.GetProperty("photos")[0].GetProperty("id").GetInt32();

        var forbidden = await _employee.DeleteAsync($"/api/work-orders/photos/{photoId}");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var removed = await _admin.DeleteAsync($"/api/work-orders/photos/{photoId}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var after = await _employee.GetFromJsonAsync<JsonElement>($"/api/work-orders/{_workOrderId}");
        Assert.Empty(after.GetProperty("photos").EnumerateArray());
    }

    [Fact]
    public async Task Photos_cannot_be_added_once_the_order_is_closed()
    {
        (await _employee.PostAsJsonAsync($"/api/work-orders/{_workOrderId}/status", new { status = 30 })).EnsureSuccessStatusCode();
        (await _employee.PostAsJsonAsync($"/api/work-orders/{_workOrderId}/resolve", new { resolutionNotes = "Casing replaced." })).EnsureSuccessStatusCode();
        (await _employee.PostAsJsonAsync($"/api/work-orders/{_workOrderId}/status", new { status = 60 })).EnsureSuccessStatusCode();

        var res = await _employee.PostAsync($"/api/work-orders/{_workOrderId}/photos", Form(("late.jpg", JpegBytes)));
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task A_photo_is_recorded_in_the_audit_log()
    {
        await _employee.PostAsync($"/api/work-orders/{_workOrderId}/photos", Form(("crack.jpg", JpegBytes)));
        var detail = await _employee.GetFromJsonAsync<JsonElement>($"/api/work-orders/{_workOrderId}");
        var photoId = detail.GetProperty("photos")[0].GetProperty("id").GetInt32();

        await using var db = fixture.CreateContext();
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE table_name = 'work_order_attachment' AND record_pk = @pk", conn);
        cmd.Parameters.AddWithValue("pk", photoId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(count > 0);
    }
}
