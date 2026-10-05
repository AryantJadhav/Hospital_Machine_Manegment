using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Boots the real API against the Testcontainers database.
///
/// Exists because a whole class of bug lives only in the endpoint layer and
/// is invisible to tests that talk to the DbContext directly. A projection
/// that EF cannot translate, a missing Include, a route that is not
/// authorised the way it looks — all of those compile, pass DbContext-level
/// tests, and then throw on the first real request.
/// </summary>
public sealed class ApiFactory(string connectionString, IReadOnlyDictionary<string, string?>? extraSettings = null)
    : WebApplicationFactory<Program>
{
    // Its own backup keys, so a test that makes a recovery key never changes a real installation's.
    private readonly string _keyDirectory = TestVault.NewKeyDirectory();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(config =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HospitalPm"] = connectionString,
            }));

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Production, so the pipeline matches what a hospital runs rather
        // than a development-only variant.
        builder.UseEnvironment("Production");

        builder.ConfigureAppConfiguration(config =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HospitalPm"] = connectionString,

                // Off in a real install. On here, so the tests of import, export, backups and updates
                // still test those; the tests of the switches turn them off through extraSettings.
                ["Features:Import"] = "true",
                ["Features:Export"] = "true",
                ["Features:Backups"] = "true",
                ["Features:Updates"] = "true",

                ["Backup:KeyDirectory"] = _keyDirectory,

                // The shipped default is plain backups. Most of these tests are about encrypted ones, so they run with it
                // on; the tests of the plain default say so through extraSettings.
                ["Backup:Encrypt"] = "true",
            });

            if (extraSettings is not null)
            {
                config.AddInMemoryCollection(extraSettings);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            try
            {
                if (Directory.Exists(_keyDirectory)) Directory.Delete(_keyDirectory, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A leftover temp folder is not worth failing a test run.
            }
        }
    }
}
