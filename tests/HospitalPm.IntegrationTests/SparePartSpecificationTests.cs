using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using HospitalPm.Domain.Equipment;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// A spare part says what it is (specification, the maker's own number) and what it fits (type of machine, make, models),
/// so the right part is found for a machine and a wrong one is not ordered.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class SparePartSpecificationTests(PostgresFixture fixture) : IAsyncLifetime, IDisposable
{
    private const string Password = "PartSpec2026!";

    private ApiFactory _factory = null!;
    private HttpClient _head = null!;
    private HttpClient _engineer = null!;
    private string _suffix = null!;
    private int _monitorTypeId;
    private int _pumpTypeId;
    private int _mx450Id;
    private int _mx800Id;
    private int _pumpId;
    private int _roomId;

    private string Make => $"Philips{_suffix}";

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(fixture.ConnectionString);
        _suffix = Guid.NewGuid().ToString("N")[..8];

        await using (var db = fixture.CreateContext())
        {
            var monitor = new EquipmentType { Code = $"mon-{_suffix}", Name = $"Monitor {_suffix}" };
            var pump = new EquipmentType { Code = $"pmp-{_suffix}", Name = $"Pump {_suffix}" };
            var room = new Domain.Locations.Location
            {
                Code = $"SPC-{_suffix}", Name = $"Spec ward {_suffix}", Level = Domain.Locations.LocationLevel.Room,
            };
            db.EquipmentTypes.AddRange(monitor, pump);
            db.Locations.Add(room);
            await db.SaveChangesAsync();
            _monitorTypeId = monitor.Id;
            _pumpTypeId = pump.Id;
            _roomId = room.Id;

            Domain.Assets.Equipment Machine(string tag, int typeId, string make, string model) => new()
            {
                AssetTag = $"{tag}-{_suffix}".ToUpperInvariant(), EquipmentTypeId = typeId, LocationId = room.Id,
                Manufacturer = make, Model = model,
            };

            var mx450 = Machine("MX450", monitor.Id, Make.ToUpperInvariant(), "MX450");     // spelled in capitals on the register
            var mx800 = Machine("MX800", monitor.Id, Make, "MX800");
            var infusion = Machine("INF", pump.Id, "Braun", "Space");
            db.Equipment.AddRange(mx450, mx800, infusion);
            await db.SaveChangesAsync();
            _mx450Id = mx450.Id;
            _mx800Id = mx800.Id;
            _pumpId = infusion.Id;
        }

        _head = await SignInAsync("spec-head", Domain.Identity.Roles.BmeHead);
        _engineer = await SignInAsync("spec-eng", Domain.Identity.Roles.BmeEngineer);
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
        _head?.Dispose();
        _engineer?.Dispose();
        _factory?.Dispose();
    }

    private async Task<int> AddAsync(string number, int? typeId = null, string? make = null, string? models = null,
        string? spec = null, string? makerNumber = null)
    {
        var res = await _head.PostAsJsonAsync("/api/spare-parts", new
        {
            partNumber = $"{number}-{_suffix}",
            name = $"Part {number}",
            equipmentTypeId = typeId,
            manufacturer = make,
            compatibleModels = models,
            specification = spec,
            manufacturerPartNumber = makerNumber,
            quantityOnHand = 4,
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    /// <summary>The part numbers (without this test's suffix) the list returns: for a search term, or for this test's own parts under a filter.</summary>
    private async Task<string[]> NumbersAsync(string? search = null, string filter = "")
    {
        var q = Uri.EscapeDataString(search ?? _suffix);
        var list = await _engineer.GetFromJsonAsync<JsonElement>($"/api/spare-parts?pageSize=200&q={q}&{filter}");
        return list.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("partNumber").GetString()!.Replace($"-{_suffix}", string.Empty, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task The_specification_make_models_and_makers_number_are_saved_and_read_back()
    {
        var id = await AddAsync("SPEC-1", _monitorTypeId, Make, "MX450, MX550", "SpO2 sensor, adult, 3 m", "M1191B");

        var seen = await _engineer.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{id}");
        Assert.Equal(Make, seen.GetProperty("manufacturer").GetString());
        Assert.Equal("MX450, MX550", seen.GetProperty("compatibleModels").GetString());
        Assert.Equal("SpO2 sensor, adult, 3 m", seen.GetProperty("specification").GetString());
        Assert.Equal("M1191B", seen.GetProperty("manufacturerPartNumber").GetString());

        // An edit that clears them clears them, the same as every other optional field of the part.
        Assert.Equal(HttpStatusCode.NoContent, (await _head.PutAsJsonAsync($"/api/spare-parts/{id}", new
        {
            partNumber = $"SPEC-1-{_suffix}", name = "Part SPEC-1", equipmentTypeId = _monitorTypeId, manufacturer = "  ",
            quantityOnHand = 4,
        })).StatusCode);
        var cleared = await _engineer.GetFromJsonAsync<JsonElement>($"/api/spare-parts/{id}");
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("manufacturer").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("specification").ValueKind);
    }

    [Fact]
    public async Task Search_finds_a_part_by_make_model_specification_or_makers_number()
    {
        await AddAsync("FIND-1", _monitorTypeId, Make, $"MX450{_suffix}, MX550{_suffix}", $"2 A slow-blow {_suffix} fuse", $"ZX-{_suffix}");
        await AddAsync("FIND-2", _pumpTypeId, "Braun", "Space", $"Syringe clamp {_suffix}");

        Assert.Equal(["FIND-1"], await NumbersAsync($"mx550{_suffix}"));
        Assert.Equal(["FIND-1"], await NumbersAsync($"slow-blow {_suffix}"));
        Assert.Equal(["FIND-1"], await NumbersAsync($"zx-{_suffix}"));
        Assert.Equal(["FIND-1"], await NumbersAsync(Make.ToUpperInvariant()));
        Assert.Equal(["FIND-2"], await NumbersAsync($"clamp {_suffix}"));
    }

    [Fact]
    public async Task The_list_can_be_narrowed_to_one_type_of_machine()
    {
        await AddAsync("TYPE-1", _monitorTypeId);
        await AddAsync("TYPE-2", _pumpTypeId);
        await AddAsync("TYPE-3");   // fits any type: not part of "Pump" or "Monitor" parts

        Assert.Equal(["TYPE-1"], await NumbersAsync(filter: $"typeId={_monitorTypeId}"));
        Assert.Equal(["TYPE-2"], await NumbersAsync(filter: $"typeId={_pumpTypeId}"));
        Assert.Equal(["TYPE-1", "TYPE-2", "TYPE-3"], await NumbersAsync());
    }

    [Fact]
    public async Task Fits_a_machine_means_its_type_make_and_model_each_match_or_are_left_open_on_the_part()
    {
        await AddAsync("FIT-ANY");                                                           // fits anything
        await AddAsync("FIT-TYPE", _monitorTypeId);                                          // any monitor
        await AddAsync("FIT-MAKE", _monitorTypeId, Make);                                    // this make of monitor (case ignored)
        await AddAsync("FIT-450", _monitorTypeId, Make, "MX450, MX550");                     // two models
        await AddAsync("FIT-800", _monitorTypeId, Make, "mx800");                            // another model, written in lower case
        await AddAsync("FIT-PUMP", _pumpTypeId);                                             // wrong type
        await AddAsync("FIT-BRAND", _monitorTypeId, "Mindray");                              // wrong make
        await AddAsync("FIT-MODELONLY", null, null, "MX450");                                // models given, any make

        // The register spells the make in capitals; the part in mixed case. Still the same make.
        Assert.Equal(["FIT-450", "FIT-ANY", "FIT-MAKE", "FIT-MODELONLY", "FIT-TYPE"],
            await NumbersAsync(filter: $"fitsEquipmentId={_mx450Id}"));
        Assert.Equal(["FIT-800", "FIT-ANY", "FIT-MAKE", "FIT-TYPE"],
            await NumbersAsync(filter: $"fitsEquipmentId={_mx800Id}"));
        // A pump: only what is for pumps or for anything.
        Assert.Equal(["FIT-ANY", "FIT-PUMP"], await NumbersAsync(filter: $"fitsEquipmentId={_pumpId}"));

        Assert.Equal(HttpStatusCode.NotFound, (await _engineer.GetAsync("/api/spare-parts?fitsEquipmentId=2000000000")).StatusCode);
    }

    [Fact]
    public async Task A_part_that_names_a_model_does_not_fit_a_machine_with_no_model_recorded()
    {
        Domain.Assets.Equipment bare;
        await using (var db = fixture.CreateContext())
        {
            bare = new Domain.Assets.Equipment
            {
                AssetTag = $"BARE-{_suffix}".ToUpperInvariant(), EquipmentTypeId = _monitorTypeId, LocationId = _roomId,
            };
            db.Equipment.Add(bare);
            await db.SaveChangesAsync();
        }

        await AddAsync("BARE-OPEN", _monitorTypeId);
        await AddAsync("BARE-MODEL", _monitorTypeId, null, "MX450");

        Assert.Equal(["BARE-OPEN"], await NumbersAsync(filter: $"fitsEquipmentId={bare.Id}"));
    }

    [Fact]
    public async Task The_suggestions_list_the_makes_and_models_already_on_the_register()
    {
        var s = await _engineer.GetFromJsonAsync<JsonElement>("/api/spare-parts/suggestions");

        var makes = s.GetProperty("manufacturers").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("Braun", makes);
        Assert.Contains(Make, makes);

        var models = s.GetProperty("models").EnumerateArray()
            .Select(x => $"{x.GetProperty("manufacturer").GetString()}|{x.GetProperty("model").GetString()}").ToList();
        Assert.Contains("Braun|Space", models);
        Assert.Contains($"{Make}|MX800", models);

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/spare-parts/suggestions")).StatusCode);
    }

    [Fact]
    public async Task Over_long_text_is_refused_and_an_engineer_cannot_write_the_specification()
    {
        var res = await _head.PostAsJsonAsync("/api/spare-parts", new
        {
            partNumber = $"LONG-{_suffix}", name = "Long", specification = new string('x', 501), quantityOnHand = 1,
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        var models = await _head.PostAsJsonAsync("/api/spare-parts", new
        {
            partNumber = $"LONGM-{_suffix}", name = "Long", compatibleModels = new string('x', 1001), quantityOnHand = 1,
        });
        Assert.Equal(HttpStatusCode.BadRequest, models.StatusCode);

        var engineer = await _engineer.PostAsJsonAsync("/api/spare-parts", new
        {
            partNumber = $"ENG-{_suffix}", name = "Not allowed", specification = "x", quantityOnHand = 1,
        });
        Assert.Equal(HttpStatusCode.Forbidden, engineer.StatusCode);
    }
}
