using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HospitalPm.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Photos on a gate pass are optional. A pass is complete without one; they can be added at any time, are kept as what the bytes
/// really are, and are removed only by an administrator.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class GatePassPhotoTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "PassPhotos2026!";

    private static readonly byte[] JpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 2, 3];
    private static readonly byte[] PngBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52];
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\n%%EOF");

    private ApiFactory _factory = null!;
    private HttpClient _engineer = null!;
    private HttpClient _head = null!;
    private string _suffix = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];
        _engineer = await SignInAsync("gpp-eng", Roles.BmeEngineer);
        _head = await SignInAsync("gpp-head", Roles.BmeHead);
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
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName = $"{prefix}-{_suffix}", password = Password });
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
        _engineer?.Dispose();
        _head?.Dispose();
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

    /// <summary>A pass for a typed line, with no photo.</summary>
    private async Task<int> PassAsync(bool returnable = true)
    {
        var res = await _engineer.PostAsJsonAsync("/api/gate-passes", new
        {
            vendorName = $"Vendor {_suffix}", isReturnable = returnable,
            items = new[] { new { description = "Patient cable", quantity = 1 } },
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    private async Task<JsonElement> PhotosOfAsync(int pass)
        => (await _engineer.GetFromJsonAsync<JsonElement>($"/api/gate-passes/{pass}")).GetProperty("photos");

    [Fact]
    public async Task A_pass_needs_no_photo_and_one_can_be_added_later()
    {
        var pass = await PassAsync();
        Assert.Equal(0, (await PhotosOfAsync(pass)).GetArrayLength());

        var saved = await _engineer.PostAsync($"/api/gate-passes/{pass}/photos", Form(("cable.jpg", JpegBytes), ("tag.png", PngBytes)));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(2, (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("photosSaved").GetInt32());

        var photos = await PhotosOfAsync(pass);
        Assert.Equal(2, photos.GetArrayLength());
        Assert.Equal("cable.jpg", photos[0].GetProperty("fileName").GetString());
        Assert.Equal("image/jpeg", photos[0].GetProperty("contentType").GetString());
        Assert.Equal("image/png", photos[1].GetProperty("contentType").GetString());
        Assert.False(string.IsNullOrWhiteSpace(photos[0].GetProperty("uploadedBy").GetString()));
    }

    [Fact]
    public async Task A_photo_can_be_added_to_a_pass_in_any_state_and_is_shown_as_the_type_it_really_is()
    {
        var pass = await PassAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await _engineer.PostAsJsonAsync($"/api/gate-passes/{pass}/return", new { })).StatusCode);

        // The photo of how it came back is taken after the pass is closed.
        Assert.Equal(HttpStatusCode.OK, (await _engineer.PostAsync($"/api/gate-passes/{pass}/photos", Form(("back.jpg", JpegBytes)))).StatusCode);

        var id = (await PhotosOfAsync(pass))[0].GetProperty("id").GetInt32();
        var shown = await _engineer.GetAsync($"/api/gate-passes/photos/{id}");
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
        Assert.Equal("image/jpeg", shown.Content.Headers.ContentType?.MediaType);
        Assert.Equal(JpegBytes, await shown.Content.ReadAsByteArrayAsync());
        Assert.Equal("nosniff", shown.Headers.GetValues("X-Content-Type-Options").Single());

        Assert.Equal(HttpStatusCode.NotFound, (await _engineer.GetAsync("/api/gate-passes/photos/2000000000")).StatusCode);
    }

    [Fact]
    public async Task Only_photos_are_accepted_by_what_the_bytes_are_and_at_most_ten()
    {
        var pass = await PassAsync();
        var url = $"/api/gate-passes/{pass}/photos";

        // A PDF, and a web page called a photo, are not photos.
        Assert.Equal(HttpStatusCode.BadRequest, (await _engineer.PostAsync(url, Form(("scan.pdf", PdfBytes)))).StatusCode);
        var page = await _engineer.PostAsync(url, Form(("photo.jpg", Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>"))));
        Assert.Equal(HttpStatusCode.BadRequest, page.StatusCode);
        Assert.Contains("not a photo", await page.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.BadRequest, (await _engineer.PostAsync(url, Form(("empty.jpg", [])))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _engineer.PostAsync(url, new MultipartFormDataContent())).StatusCode);
        Assert.Equal(0, (await PhotosOfAsync(pass)).GetArrayLength());

        for (var n = 0; n < 10; n++)
        {
            Assert.Equal(HttpStatusCode.OK, (await _engineer.PostAsync(url, Form(($"p{n}.jpg", JpegBytes)))).StatusCode);
        }

        var eleventh = await _engineer.PostAsync(url, Form(("p11.jpg", JpegBytes)));
        Assert.Equal(HttpStatusCode.BadRequest, eleventh.StatusCode);
        Assert.Contains("at most 10", await eleventh.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.NotFound, (await _engineer.PostAsync("/api/gate-passes/2000000000/photos", Form(("a.jpg", JpegBytes)))).StatusCode);
    }

    [Fact]
    public async Task Photos_are_added_by_those_who_write_passes_and_removed_only_by_an_administrator()
    {
        var pass = await PassAsync();
        Assert.Equal(HttpStatusCode.OK, (await _engineer.PostAsync($"/api/gate-passes/{pass}/photos", Form(("a.jpg", JpegBytes)))).StatusCode);
        var id = (await PhotosOfAsync(pass))[0].GetProperty("id").GetInt32();

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/gate-passes/photos/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/gate-passes/{pass}/photos", Form(("a.jpg", JpegBytes)))).StatusCode);

        // An engineer can add, but removing a photo is for the head.
        Assert.Equal(HttpStatusCode.Forbidden, (await _engineer.DeleteAsync($"/api/gate-passes/photos/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _head.DeleteAsync($"/api/gate-passes/photos/{id}")).StatusCode);
        Assert.Equal(0, (await PhotosOfAsync(pass)).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await _head.DeleteAsync($"/api/gate-passes/photos/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_photo_is_a_record_postgres_audits_it_and_it_cannot_be_edited()
    {
        var pass = await PassAsync(returnable: false);
        Assert.Equal(HttpStatusCode.OK, (await _engineer.PostAsync($"/api/gate-passes/{pass}/photos", Form(("a.jpg", JpegBytes)))).StatusCode);
        var id = (await PhotosOfAsync(pass))[0].GetProperty("id").GetInt32();

        await using var db = fixture.CreateContext();
        var edit = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE gate_pass_attachment SET file_name = 'other.jpg' WHERE id = {0}", id));
        Assert.Contains("cannot be changed", edit.ToString(), StringComparison.Ordinal);

        var audited = await db.Database.SqlQueryRaw<long>(
            "SELECT count(*) AS \"Value\" FROM audit_log WHERE table_name = 'gate_pass_attachment' AND record_pk = {0}", id.ToString()).SingleAsync();
        Assert.Equal(1, audited);
    }
}
