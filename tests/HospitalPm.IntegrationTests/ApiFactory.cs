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
            });

            if (extraSettings is not null)
            {
                config.AddInMemoryCollection(extraSettings);
            }
        });
    }
}
