using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Licensing;
using HospitalPm.Domain.Locations;
using HospitalPm.Infrastructure.Licensing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// What happens when a licence ends: two weeks of grace, then read-only.
///
/// The rules that matter are the ones that protect the hospital: reading never
/// stops, and neither does signing in, taking a backup, or installing the
/// renewal that fixes it.
/// </summary>
public sealed class LicenceGraceTests : IDisposable
{
    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly List<string> _paths = [];

    private string PublicKey => Convert.ToBase64String(_signer.ExportSubjectPublicKeyInfo());

    private static readonly DateOnly End = new(2026, 6, 1);

    public void Dispose()
    {
        _signer.Dispose();
        foreach (var path in _paths.SelectMany(p => new[] { p, p + ".state" }))
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private string TempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hospitalpm-{Guid.NewGuid():N}.licence");
        _paths.Add(path);
        return path;
    }

    private string Sign(Licence licence)
    {
        var payload = LicenceFile.Serialise(licence);
        return LicenceFile.Format(
            payload,
            _signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    private static Licence Fixed(DateOnly? expires) => new(
        Guid.NewGuid(), "Sahyadri Hospital, Pune", new DateOnly(2026, 1, 1), expires, [], null, null);

    private static Licence Lasting(int days, Guid? id = null) => new(
        id ?? Guid.NewGuid(), "Sahyadri Hospital, Pune", new DateOnly(2026, 1, 1), null, [], null, null, days);

    private LicenceVerifier Verifier => new(PublicKey);

    private sealed class MovableClock(DateOnly start) : TimeProvider
    {
        private DateTimeOffset _now = new(start.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);

        public void Set(DateOnly day) => _now = new(day.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    // --- The grace period -----------------------------------------------------

    [Fact]
    public void The_last_day_is_still_valid_and_the_day_after_is_expired_but_working()
    {
        var file = Sign(Fixed(End));

        Assert.Equal(LicenceState.Valid, Verifier.Verify(file, End).State);

        var after = Verifier.Verify(file, End.AddDays(1));
        Assert.Equal(LicenceState.Expired, after.State);
        Assert.False(after.IsReadOnly);
    }

    [Fact]
    public void Fourteen_days_of_grace_then_read_only_on_the_fifteenth()
    {
        var file = Sign(Fixed(End));

        Assert.Equal(LicenceState.Expired, Verifier.Verify(file, End.AddDays(14)).State);

        var readOnly = Verifier.Verify(file, End.AddDays(15));
        Assert.Equal(LicenceState.ReadOnly, readOnly.State);
        Assert.True(readOnly.IsReadOnly);
        Assert.Equal(End.AddDays(15), readOnly.ReadOnlyFrom);
        Assert.Contains("read-only", readOnly.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sahyadri Hospital, Pune", readOnly.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_expired_message_says_when_recording_will_stop()
    {
        var status = Verifier.Verify(Sign(Fixed(End)), End.AddDays(3));

        Assert.Contains(End.AddDays(14).ToString("dd/MM/yyyy"), status.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_grace_period_can_be_set_to_nothing()
    {
        var status = Verifier.Verify(Sign(Fixed(End)), End.AddDays(1), graceDays: 0);

        Assert.Equal(LicenceState.ReadOnly, status.State);
    }

    [Fact]
    public void A_perpetual_licence_is_never_read_only()
    {
        var status = Verifier.Verify(Sign(Fixed(null)), new DateOnly(2099, 1, 1));

        Assert.Equal(LicenceState.Valid, status.State);
        Assert.Null(status.EffectiveExpiry);
    }

    [Fact]
    public void A_licence_close_to_its_end_says_how_many_days_are_left()
    {
        var status = Verifier.Verify(Sign(Fixed(End)), End.AddDays(-5));

        Assert.Equal(LicenceState.Valid, status.State);
        Assert.Contains("5 days left", status.Message, StringComparison.Ordinal);
    }

    // --- A key that carries a length of time ------------------------------------

    [Fact]
    public void A_duration_key_ends_that_many_days_after_it_began()
    {
        var began = new DateOnly(2026, 9, 1);
        var file = Sign(Lasting(84));
        var end = began.AddDays(84);

        Assert.Equal(end, Verifier.Verify(file, began, began).EffectiveExpiry);
        Assert.Equal(LicenceState.Valid, Verifier.Verify(file, end, began).State);
        Assert.Equal(LicenceState.Expired, Verifier.Verify(file, end.AddDays(1), began).State);
        Assert.Equal(LicenceState.ReadOnly, Verifier.Verify(file, end.AddDays(15), began).State);
    }

    [Fact]
    public void A_key_with_both_a_date_and_a_length_ends_at_the_earlier()
    {
        var began = new DateOnly(2026, 9, 1);
        var licence = Lasting(84) with { ExpiresOn = new DateOnly(2026, 10, 1) };

        Assert.Equal(new DateOnly(2026, 10, 1), Verifier.Verify(Sign(licence), began, began).EffectiveExpiry);
    }

    [Fact]
    public void The_service_starts_a_duration_key_when_it_is_installed_and_not_again()
    {
        var clock = new MovableClock(new DateOnly(2026, 9, 1));
        var service = new LicenceService(
            Options.Create(new LicenceOptions { PublicKey = PublicKey, Path = TempPath() }), clock);
        var file = Sign(Lasting(30));

        var (saved, first) = service.Install(file);

        Assert.True(saved);
        Assert.Equal(new DateOnly(2026, 10, 1), first.EffectiveExpiry);

        // Pasted in again three weeks later: the clock does not start over.
        clock.Set(new DateOnly(2026, 9, 22));
        var (_, again) = service.Install(file);
        Assert.Equal(new DateOnly(2026, 10, 1), again.EffectiveExpiry);
        Assert.Equal(new DateOnly(2026, 10, 1), service.Current().EffectiveExpiry);
    }

    [Fact]
    public void A_new_duration_key_starts_fresh()
    {
        var clock = new MovableClock(new DateOnly(2026, 9, 1));
        var service = new LicenceService(
            Options.Create(new LicenceOptions { PublicKey = PublicKey, Path = TempPath() }), clock);

        service.Install(Sign(Lasting(30)));

        clock.Set(new DateOnly(2026, 10, 20));
        Assert.Equal(LicenceState.ReadOnly, service.Current().State);

        var (_, renewed) = service.Install(Sign(Lasting(90)));

        Assert.Equal(LicenceState.Valid, renewed.State);
        Assert.Equal(new DateOnly(2027, 1, 18), renewed.EffectiveExpiry);
    }

    [Fact]
    public void A_duration_key_placed_on_disk_by_hand_starts_the_first_time_it_is_read()
    {
        var path = TempPath();
        File.WriteAllText(path, Sign(Lasting(30)));
        var clock = new MovableClock(new DateOnly(2026, 9, 1));
        var service = new LicenceService(Options.Create(new LicenceOptions { PublicKey = PublicKey, Path = path }), clock);

        Assert.Equal(new DateOnly(2026, 10, 1), service.Current().EffectiveExpiry);

        clock.Set(new DateOnly(2026, 9, 20));
        Assert.Equal(new DateOnly(2026, 10, 1), service.Current().EffectiveExpiry);
    }

    // --- Putting the clock back -------------------------------------------------

    [Fact]
    public void Putting_the_clock_back_does_not_bring_an_expired_licence_back()
    {
        var path = TempPath();
        File.WriteAllText(path, Sign(Fixed(End)));
        var clock = new MovableClock(End.AddDays(40));
        var service = new LicenceService(Options.Create(new LicenceOptions { PublicKey = PublicKey, Path = path }), clock);

        Assert.Equal(LicenceState.ReadOnly, service.Current().State);

        clock.Set(End.AddDays(-30));

        Assert.Equal(LicenceState.ReadOnly, service.Current().State);
    }

    [Fact]
    public void A_damaged_memory_file_is_ignored_rather_than_breaking_the_licence()
    {
        var path = TempPath();
        File.WriteAllText(path, Sign(Fixed(new DateOnly(2099, 1, 1))));
        File.WriteAllText(path + ".state", "{ this is not json");
        var service = new LicenceService(
            Options.Create(new LicenceOptions { PublicKey = PublicKey, Path = path }), TimeProvider.System);

        Assert.Equal(LicenceState.Valid, service.Current().State);
    }

    [Fact]
    public void Replacing_the_file_is_noticed_straight_away()
    {
        var path = TempPath();
        File.WriteAllText(path, Sign(Fixed(End)));
        var service = new LicenceService(
            Options.Create(new LicenceOptions { PublicKey = PublicKey, Path = path }), new MovableClock(End.AddDays(40)));

        Assert.Equal(LicenceState.ReadOnly, service.Current().State);

        File.WriteAllText(path, Sign(Fixed(End.AddYears(2))));

        Assert.Equal(LicenceState.Valid, service.Current().State);
    }
}

/// <summary>
/// Over HTTP: read-only refuses what records something, and nothing else.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class LicenceReadOnlyApiTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "ReadOnly2026!";

    private readonly ECDsa _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _licencePath = Path.Combine(Path.GetTempPath(), $"hospitalpm-{Guid.NewGuid():N}.licence");

    private ApiFactory _factory = null!;
    private HttpClient _admin = null!;
    private HttpClient _employee = null!;
    private int _equipmentId;

    public async Task InitializeAsync()
    {
        // Expired well beyond the grace period, so the software is read-only.
        File.WriteAllText(_licencePath, Sign(new Licence(
            Guid.NewGuid(), "Sahyadri Hospital, Pune", new DateOnly(2025, 1, 1),
            new DateOnly(2025, 6, 1), [], null, null)));

        _factory = new ApiFactory(fixture.ConnectionString, new Dictionary<string, string?>
        {
            ["Licence:PublicKey"] = Convert.ToBase64String(_signer.ExportSubjectPublicKeyInfo()),
            ["Licence:Path"] = _licencePath,
        });

        var suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var room = new Location { Code = $"RO-{suffix}", Name = $"Ward {suffix}", Level = LocationLevel.Room };
            db.Locations.Add(room);
            await db.SaveChangesAsync();

            var type = await db.EquipmentTypes.FirstAsync(t => t.Code == "ventilator");
            var equipment = new Domain.Assets.Equipment
            {
                AssetTag = $"RO-{suffix}".ToUpperInvariant(), EquipmentTypeId = type.Id, LocationId = room.Id,
            };
            db.Equipment.Add(equipment);
            await db.SaveChangesAsync();
            _equipmentId = equipment.Id;
        }

        _admin = await SignedInAsync($"ro-adm-{suffix}", Roles.Admin);
        _employee = await SignedInAsync($"ro-emp-{suffix}", Roles.Employee);
    }

    private string Sign(Licence licence)
    {
        var payload = LicenceFile.Serialise(licence);
        return LicenceFile.Format(
            payload,
            _signer.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    private async Task<HttpClient> SignedInAsync(string userName, string role)
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser { UserName = userName, FullName = userName, IsActive = true };
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
        _signer.Dispose();
        foreach (var p in new[] { _licencePath, _licencePath + ".state" })
        {
            try { if (File.Exists(p)) File.Delete(p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private Task<HttpResponseMessage> ReportFaultAsync(HttpClient who) =>
        who.PostAsJsonAsync("/api/work-orders", new
        {
            equipmentId = _equipmentId,
            faultDescription = "Alarm sounds with no cause",
            priority = 20,
        });

    [Fact]
    public async Task Recording_something_new_is_refused_with_the_reason()
    {
        var refused = await ReportFaultAsync(_employee);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("licence-read-only", body.GetProperty("code").GetString());
        Assert.Contains("read-only", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/api/dashboard")]
    [InlineData("/api/equipment")]
    [InlineData("/api/work-orders")]
    [InlineData("/api/pm/tasks")]
    public async Task Reading_still_works(string path)
    {
        var response = await _employee.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Printing_a_report_still_works()
    {
        var response = await _admin.GetAsync(
            "/api/reports/pm-compliance/report.csv?from=2026-01-01&to=2026-01-31");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Signing_in_still_works()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var again = await SignedInAsync($"ro-late-{suffix}", Roles.Employee);

        Assert.NotNull(again.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task A_backup_can_still_be_taken()
    {
        var response = await _admin.PostAsync("/api/admin/backups/run", null);

        // Whether pg_dump exists on this machine is not what is being asked;
        // only that the licence is not what refused it.
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Installing_a_renewal_lifts_it_at_once()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await ReportFaultAsync(_employee)).StatusCode);

        var renewal = Sign(new Licence(
            Guid.NewGuid(), "Sahyadri Hospital, Pune", new DateOnly(2026, 1, 1), new DateOnly(2099, 1, 1), [], null, null));

        var installed = await _admin.PostAsJsonAsync("/api/admin/licence", new { licence = renewal });
        Assert.Equal(HttpStatusCode.OK, installed.StatusCode);

        var reported = await ReportFaultAsync(_employee);
        Assert.Equal(HttpStatusCode.Created, reported.StatusCode);
    }

    [Fact]
    public async Task Every_signed_in_person_is_told_and_the_licence_details_stay_with_administrators()
    {
        var banner = await _employee.GetFromJsonAsync<JsonElement>("/api/licence/banner");

        Assert.True(banner.GetProperty("show").GetBoolean());
        Assert.True(banner.GetProperty("readOnly").GetBoolean());
        Assert.Contains("read-only", banner.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);

        var details = await _employee.GetAsync("/api/admin/licence");
        Assert.Equal(HttpStatusCode.Forbidden, details.StatusCode);
    }
}
