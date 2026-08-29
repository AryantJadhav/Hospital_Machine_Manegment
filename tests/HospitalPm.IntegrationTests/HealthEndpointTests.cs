using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// Boots the real API in-process. This is the week-1 equivalent of the
/// Phase 0 gate: if the host cannot start, everything downstream is moot.
/// </summary>
public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Health_returns_ok()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_does_not_require_a_database()
    {
        // The liveness probe must stay answerable when Postgres is down,
        // otherwise the Phase 2 diagnostics page cannot tell "process dead"
        // apart from "database unreachable". No DB is configured in this
        // test host, so a 200 here proves the endpoint is independent of it.
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
