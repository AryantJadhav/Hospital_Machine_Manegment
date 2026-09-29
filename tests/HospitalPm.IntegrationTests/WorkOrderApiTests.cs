using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Inventory;
using HospitalPm.Domain.Locations;
using HospitalPm.Domain.WorkOrders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Over HTTP, because the detail endpoint once returned an asset tag with no
/// equipment type or location and the UI rendered "BME-0001 · ·". Every
/// DbContext-level test passed; only a real response shows a missing field.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class WorkOrderApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;
    private int _workOrderId;
    private int _equipmentId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _client = _factory.CreateClient();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userName = $"woapi-{suffix}";
        const string password = "WorkOrders2026!";

        await using var db = fixture.CreateContext();

        var room = new Location
        {
            Code = $"WOA-{suffix}",
            Name = $"Ward {suffix}",
            Level = LocationLevel.Room,
        };
        db.Locations.Add(room);
        await db.SaveChangesAsync();

        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");

        var equipment = new Domain.Assets.Equipment
        {
            AssetTag = $"WOA-{suffix}".ToUpperInvariant(),
            EquipmentTypeId = type.Id,
            LocationId = room.Id,
        };
        db.Equipment.Add(equipment);
        await db.SaveChangesAsync();
        _equipmentId = equipment.Id;

        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider
            .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();

        var user = new Infrastructure.Identity.ApplicationUser
        {
            UserName = userName,
            FullName = "Work Order API User",
        };
        await users.CreateAsync(user, password);
        await users.AddToRoleAsync(user, Domain.Identity.Roles.Admin);

        var login = await _client.PostAsJsonAsync("/api/auth/login", new { userName, password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());

        var created = await _client.PostAsJsonAsync("/api/work-orders", new
        {
            equipmentId = _equipmentId,
            faultDescription = "Display intermittently blank",
            priority = 30,
        });
        created.EnsureSuccessStatusCode();

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        _workOrderId = body.GetProperty("id").GetInt32();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Fact]
    public async Task Reporting_a_fault_returns_a_quotable_number()
    {
        var res = await _client.GetAsync($"/api/work-orders/{_workOrderId}");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Matches(@"^WO-\d{4}-\d{6}$", body.GetProperty("number").GetString()!);
    }

    [Fact]
    public async Task The_detail_endpoint_returns_equipment_type_and_location()
    {
        var res = await _client.GetAsync($"/api/work-orders/{_workOrderId}");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        // The bug this class exists for.
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("assetTag").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("equipmentTypeName").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("locationName").GetString()));
    }

    [Fact]
    public async Task The_detail_endpoint_tells_the_client_what_it_may_do_next()
    {
        var res = await _client.GetAsync($"/api/work-orders/{_workOrderId}");
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        var allowed = body.GetProperty("allowedTransitions").EnumerateArray()
            .Select(x => x.GetInt32()).ToList();

        // Offering a button that returns 409 teaches people to distrust the
        // buttons, so the UI renders only what the server will accept.
        Assert.Contains((int)WorkOrderStatus.Assigned, allowed);
        Assert.Contains((int)WorkOrderStatus.Cancelled, allowed);
        Assert.DoesNotContain((int)WorkOrderStatus.Closed, allowed);
    }

    [Fact]
    public async Task An_invalid_transition_is_refused_with_a_readable_message()
    {
        var res = await _client.PostAsJsonAsync(
            $"/api/work-orders/{_workOrderId}/status",
            new { status = (int)WorkOrderStatus.Closed });

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var message = body.GetProperty("error").GetString()!;

        Assert.Contains("can only move to", message, StringComparison.Ordinal);
        Assert.DoesNotContain("InProgress", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolving_without_notes_is_refused()
    {
        var res = await _client.PostAsJsonAsync(
            $"/api/work-orders/{_workOrderId}/resolve",
            new { resolutionNotes = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_service_report_renders_for_an_unresolved_work_order()
    {
        var res = await _client.GetAsync($"/api/reports/work-orders/{_workOrderId}/report.pdf");

        // An engineer often needs the paperwork before the job is closed.
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/pdf", res.Content.Headers.ContentType?.MediaType);

        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF"u8.ToArray(), bytes.Take(4).ToArray());
    }

    [Fact]
    public async Task The_dashboard_answers_in_one_call()
    {
        var res = await _client.GetAsync("/api/dashboard");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("equipment").GetProperty("total").GetInt32() > 0);
        Assert.True(body.GetProperty("workOrders").GetProperty("open").GetInt32() > 0);
        // Reported with the machine out of service, so it must be counted down.
        Assert.True(body.GetProperty("workOrders").GetProperty("machinesDown").GetInt32() > 0);
    }

    [Fact]
    public async Task A_certificate_is_404_when_the_pm_was_never_completed()
    {
        var res = await _client.GetAsync("/api/reports/pm/999999/certificate.pdf");

        // Not a 500. A biomedical head clicking Certificate on an open PM
        // should be told why, not shown an error page.
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // A genuinely decodable 1x1 pixel JPEG - the report embeds it via QuestPDF's
    // Image element, which needs real image bytes and not just a valid signature.
    private static readonly byte[] JpegBytes = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgICAgMCAgIDAwMDBAYEBAQEBAgGBgUGCQgKCgkICQkKDA8MCgsOCwkJDRENDg8QEBEQ" +
        "CgwSExIQEw8QEBD/2wBDAQMDAwQDBAgEBAgQCwkLEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQ" +
        "EBD/wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAj/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/8QAFQEBAQAAAAAA" +
        "AAAAAAAAAAAAAAX/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIRAxEAPwCdABmX/9k=");

    [Fact]
    public async Task The_service_report_prints_parts_used_and_photos()
    {
        int partId;
        await using (var db = fixture.CreateContext())
        {
            var part = new SparePart
            {
                PartNumber = $"WOA-PART-{Guid.NewGuid():N}"[..20],
                Name = "Flow sensor",
                QuantityOnHand = 5,
                ReorderLevel = 1,
                UnitCost = 4200m,
            };
            db.SpareParts.Add(part);
            await db.SaveChangesAsync();
            partId = part.Id;
        }

        (await _client.PostAsJsonAsync($"/api/work-orders/{_workOrderId}/parts",
            new { sparePartId = partId, quantityUsed = 1 })).EnsureSuccessStatusCode();

        var form = new MultipartFormDataContent();
        var photoPart = new ByteArrayContent(JpegBytes);
        photoPart.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(photoPart, "files", "fault.jpg");
        (await _client.PostAsync($"/api/work-orders/{_workOrderId}/photos", form)).EnsureSuccessStatusCode();

        var res = await _client.GetAsync($"/api/reports/work-orders/{_workOrderId}/report.pdf");

        // Renders through the real database join and the real photo bytes,
        // not just the in-memory document tests - this is where a bad
        // navigation property or a lazily-thrown QuestPDF error would show up.
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal("%PDF"u8.ToArray(), bytes.Take(4).ToArray());
    }

    [Fact]
    public async Task The_list_can_be_narrowed_to_one_priority()
    {
        // The fixture already reported a High fault; add a Critical one against
        // the same machine so the filter has two priorities to tell apart.
        var critical = await _client.PostAsJsonAsync("/api/work-orders", new
        {
            equipmentId = _equipmentId,
            faultDescription = "Ventilator alarm will not silence",
            priority = (int)WorkOrderPriority.Critical,
        });
        critical.EnsureSuccessStatusCode();
        var criticalId = (await critical.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var filtered = await _client.GetFromJsonAsync<JsonElement>("/api/work-orders?priority=40");
        var items = filtered.GetProperty("items").EnumerateArray().ToList();

        Assert.Contains(items, i => i.GetProperty("id").GetInt32() == criticalId);
        Assert.DoesNotContain(items, i => i.GetProperty("id").GetInt32() == _workOrderId);
        Assert.All(items, i => Assert.Equal((int)WorkOrderPriority.Critical, i.GetProperty("priority").GetInt32()));
    }
}
