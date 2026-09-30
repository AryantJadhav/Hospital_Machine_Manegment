using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Assets;
using HospitalPm.Domain.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>Renewing a machine's insurance keeps the policy it replaces.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class EquipmentInsuranceRenewalTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "Renewal2026!";

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private string _suffix = null!;
    private int _insuredId;
    private int _uninsuredId;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        _admin = await SignInAsync("ren-admin", Domain.Identity.Roles.Admin);
        _employee = await SignInAsync("ren-emp", Domain.Identity.Roles.Employee);

        await using var db = fixture.CreateContext();

        var room = new Location { Code = $"REN-{_suffix}", Name = $"Room {_suffix}", Level = LocationLevel.Room };
        db.Locations.Add(room);
        await db.SaveChangesAsync();

        var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");

        var insured = new Equipment
        {
            AssetTag = $"REN-{_suffix}".ToUpperInvariant(),
            EquipmentTypeId = type.Id,
            LocationId = room.Id,
            IsInsured = true,
            InsuranceProvider = "Star Health",
            InsurancePolicyNumber = "POL-1",
            InsuranceExpiryDate = new DateOnly(2027, 3, 31),
            InsuranceCost = 5_000m,
        };
        var uninsured = new Equipment
        {
            AssetTag = $"RUN-{_suffix}".ToUpperInvariant(),
            EquipmentTypeId = type.Id,
            LocationId = room.Id,
        };
        db.Equipment.AddRange(insured, uninsured);
        await db.SaveChangesAsync();
        _insuredId = insured.Id;
        _uninsuredId = uninsured.Id;
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

    private static object Renewal(string? provider = "New India", string expiry = "2028-03-31", decimal? cost = 6_000m) => new
    {
        insuranceProvider = provider,
        insurancePolicyNumber = "POL-2",
        insuranceExpiryDate = expiry,
        insuranceCost = cost,
    };

    private Task<HttpResponseMessage> RenewAsync(HttpClient client, int id, object body)
        => client.PostAsJsonAsync($"/api/equipment/{id}/insurance/renew", body);

    [Fact]
    public async Task Renewing_keeps_the_old_policy_and_puts_the_new_one_in_its_place()
    {
        var res = await RenewAsync(_admin, _insuredId, Renewal());
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        await using var db = fixture.CreateContext();

        var machine = await db.Equipment.AsNoTracking().SingleAsync(e => e.Id == _insuredId);
        Assert.Equal("New India", machine.InsuranceProvider);
        Assert.Equal("POL-2", machine.InsurancePolicyNumber);
        Assert.Equal(new DateOnly(2028, 3, 31), machine.InsuranceExpiryDate);
        Assert.Equal(6_000m, machine.InsuranceCost);
        Assert.True(machine.IsInsured);

        var old = await db.PastInsurancePolicies.AsNoTracking().SingleAsync(p => p.EquipmentId == _insuredId);
        Assert.Equal("Star Health", old.Provider);
        Assert.Equal("POL-1", old.PolicyNumber);
        Assert.Equal(new DateOnly(2027, 3, 31), old.ExpiryDate);
        Assert.Equal(5_000m, old.Cost);
    }

    [Fact]
    public async Task Renewing_twice_keeps_both_earlier_policies()
    {
        (await RenewAsync(_admin, _insuredId, Renewal(expiry: "2028-03-31", cost: 6_000m))).EnsureSuccessStatusCode();
        (await RenewAsync(_admin, _insuredId, Renewal(provider: "Bajaj", expiry: "2029-03-31", cost: 7_000m))).EnsureSuccessStatusCode();

        await using var db = fixture.CreateContext();

        var past = await db.PastInsurancePolicies.AsNoTracking()
            .Where(p => p.EquipmentId == _insuredId).OrderBy(p => p.ExpiryDate).ToListAsync();

        Assert.Equal([5_000m, 6_000m], past.Select(p => p.Cost).ToArray());
    }

    [Fact]
    public async Task An_employee_cannot_renew_insurance()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await RenewAsync(_employee, _insuredId, Renewal())).StatusCode);
    }

    [Fact]
    public async Task A_machine_that_is_not_insured_has_nothing_to_renew()
    {
        var res = await RenewAsync(_admin, _uninsuredId, Renewal());

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task A_machine_that_does_not_exist_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await RenewAsync(_admin, 2_000_000_000, Renewal())).StatusCode);
    }

    [Fact]
    public async Task A_new_policy_that_does_not_run_past_the_old_one_is_refused_and_nothing_changes()
    {
        var res = await RenewAsync(_admin, _insuredId, Renewal(expiry: "2027-03-31"));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        await using var db = fixture.CreateContext();
        Assert.Empty(await db.PastInsurancePolicies.Where(p => p.EquipmentId == _insuredId).ToListAsync());
        Assert.Equal("Star Health", (await db.Equipment.AsNoTracking().SingleAsync(e => e.Id == _insuredId)).InsuranceProvider);
    }

    [Fact]
    public async Task The_insurer_and_a_sensible_cost_are_required()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await RenewAsync(_admin, _insuredId, Renewal(provider: "  "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RenewAsync(_admin, _insuredId, Renewal(cost: -1m))).StatusCode);
    }

    [Fact]
    public async Task A_renewal_can_leave_the_cost_unrecorded()
    {
        (await RenewAsync(_admin, _insuredId, Renewal(cost: null))).EnsureSuccessStatusCode();

        await using var db = fixture.CreateContext();
        Assert.Null((await db.Equipment.AsNoTracking().SingleAsync(e => e.Id == _insuredId)).InsuranceCost);
    }

    [Fact]
    public async Task A_past_policy_cannot_be_edited_afterwards()
    {
        (await RenewAsync(_admin, _insuredId, Renewal())).EnsureSuccessStatusCode();

        await using var db = fixture.CreateContext();
        var id = (await db.PastInsurancePolicies.AsNoTracking().SingleAsync(p => p.EquipmentId == _insuredId)).Id;

        // It is what was in force and what it cost; the database refuses to rewrite it.
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync("UPDATE equipment_insurance_history SET cost = 1 WHERE id = {0}", id));

        Assert.Contains("cannot be changed", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_renewal_is_recorded_in_the_audit_log()
    {
        (await RenewAsync(_admin, _insuredId, Renewal())).EnsureSuccessStatusCode();

        await using var db = fixture.CreateContext();
        var id = (await db.PastInsurancePolicies.AsNoTracking().SingleAsync(p => p.EquipmentId == _insuredId)).Id;

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM audit_log WHERE table_name = 'equipment_insurance_history' AND record_pk = @pk", conn);
        cmd.Parameters.AddWithValue("pk", id.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0);
    }
}
