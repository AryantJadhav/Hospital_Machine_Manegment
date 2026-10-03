using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hangfire;
using HospitalPm.Domain.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Import, export, backups and updates can be switched off for everyone, Administrators included.
///
/// Off is not hidden: the routes answer as though they did not exist, so nobody reaches them by
/// typing the address or calling the API. Each part has its own switch. The nightly backup is not
/// one of them and keeps running, because a hospital that switched off the Backups page should not
/// find out later that it has also stopped having backups.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class FeatureSwitchTests(PostgresFixture fixture) : IDisposable
{
    private const string Password = "FeatureSwitch2026!";

    private readonly List<IDisposable> _toDispose = [];

    public void Dispose()
    {
        foreach (var d in _toDispose)
        {
            d.Dispose();
        }
    }

    private static Dictionary<string, string?> Only(params string[] on) => new()
    {
        ["Features:Import"] = on.Contains("Import") ? "true" : "false",
        ["Features:Export"] = on.Contains("Export") ? "true" : "false",
        ["Features:Backups"] = on.Contains("Backups") ? "true" : "false",
        ["Features:Updates"] = on.Contains("Updates") ? "true" : "false",
    };

    private async Task<(ApiFactory Factory, HttpClient Admin)> StartAsync(Dictionary<string, string?> settings)
    {
        var factory = new ApiFactory(fixture.ConnectionString, settings);
        _toDispose.Add(factory);

        var userName = $"fs-{Guid.NewGuid():N}"[..14];
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider
                .GetRequiredService<UserManager<Infrastructure.Identity.ApplicationUser>>();
            var user = new Infrastructure.Identity.ApplicationUser { UserName = userName, FullName = userName, IsActive = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(user, Roles.Developer)).Succeeded);
        }

        var client = factory.CreateClient();
        _toDispose.Add(client);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { userName, password = Password });
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        return (factory, client);
    }

    private static readonly (string Feature, HttpMethod Method, string Path)[] Routes =
    [
        ("Import", HttpMethod.Get, "/api/equipment/import/template"),
        ("Import", HttpMethod.Get, "/api/equipment/import/locations/template"),
        ("Import", HttpMethod.Post, "/api/equipment/import/validate"),
        ("Import", HttpMethod.Post, "/api/equipment/import/commit"),
        ("Import", HttpMethod.Post, "/api/equipment/import/locations/commit"),
        ("Export", HttpMethod.Get, "/api/admin/export"),
        ("Backups", HttpMethod.Get, "/api/admin/backups"),
        ("Backups", HttpMethod.Post, "/api/admin/backups/run"),
        ("Backups", HttpMethod.Post, "/api/admin/backups/1/restore"),
        ("Updates", HttpMethod.Get, "/api/admin/update"),
        ("Updates", HttpMethod.Post, "/api/admin/update/check"),
        ("Updates", HttpMethod.Post, "/api/admin/update/install"),
    ];

    private static Task<HttpResponseMessage> Call(HttpClient client, HttpMethod method, string path)
        => client.SendAsync(new HttpRequestMessage(method, path));

    [Fact]
    public async Task With_everything_off_an_administrator_cannot_reach_any_of_it()
    {
        var (_, admin) = await StartAsync(Only());

        foreach (var (_, method, path) in Routes)
        {
            var res = await Call(admin, method, path);

            Assert.True(res.StatusCode == HttpStatusCode.NotFound, $"{method} {path} answered {(int)res.StatusCode}");
            Assert.Contains("switched off", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_features_endpoint_says_what_is_on()
    {
        var (_, off) = await StartAsync(Only());
        var (_, mixed) = await StartAsync(Only("Export", "Updates"));

        var none = await off.GetFromJsonAsync<JsonElement>("/api/features");
        Assert.False(none.GetProperty("import").GetBoolean());
        Assert.False(none.GetProperty("export").GetBoolean());
        Assert.False(none.GetProperty("backups").GetBoolean());
        Assert.False(none.GetProperty("updates").GetBoolean());

        var some = await mixed.GetFromJsonAsync<JsonElement>("/api/features");
        Assert.False(some.GetProperty("import").GetBoolean());
        Assert.True(some.GetProperty("export").GetBoolean());
        Assert.False(some.GetProperty("backups").GetBoolean());
        Assert.True(some.GetProperty("updates").GetBoolean());
    }

    [Fact]
    public async Task Somebody_who_is_not_signed_in_is_asked_to_sign_in_and_is_not_told_what_is_off()
    {
        var (factory, _) = await StartAsync(Only());
        using var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/features")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/export")).StatusCode);
    }

    [Theory]
    [InlineData("Import")]
    [InlineData("Export")]
    [InlineData("Backups")]
    [InlineData("Updates")]
    public async Task Each_part_has_its_own_switch(string on)
    {
        var (_, admin) = await StartAsync(Only(on));

        foreach (var (feature, method, path) in Routes)
        {
            var res = await Call(admin, method, path);

            if (feature == on)
            {
                // On: it gets past the switch. Whatever it then says, it is not "switched off".
                Assert.False(
                    res.StatusCode == HttpStatusCode.NotFound
                    && (await res.Content.ReadAsStringAsync()).Contains("switched off", StringComparison.Ordinal),
                    $"{method} {path} is on but was refused as switched off");
            }
            else
            {
                Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
                Assert.Contains("switched off", await res.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task With_the_parts_off_the_things_that_are_not_switched_are_untouched()
    {
        var (_, admin) = await StartAsync(Only());

        // The register, the PM list and the dashboard are nothing to do with these switches.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/equipment")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/pm/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/diagnostics")).StatusCode);
    }

    [Fact]
    public async Task The_nightly_backup_still_runs_when_the_Backups_switch_is_off()
    {
        var (factory, _) = await StartAsync(Only());

        using var connection = factory.Services.GetRequiredService<JobStorage>().GetConnection();

        Assert.Contains(Hangfire.Storage.StorageConnectionExtensions.GetRecurringJobs(connection), j => j.Id == "nightly-backup");
    }

    [Fact]
    public void A_real_install_starts_with_all_four_switched_off()
    {
        // The settings file the product ships with, read as it is.
        var root = AppContext.BaseDirectory;
        var json = File.ReadAllText(Path.Combine(root, "appsettings.json"));
        var features = JsonDocument.Parse(json).RootElement.GetProperty("Features");

        foreach (var name in new[] { "Import", "Export", "Backups", "Updates" })
        {
            Assert.False(features.GetProperty(name).GetBoolean(), $"{name} should ship switched off");
        }
    }
}
