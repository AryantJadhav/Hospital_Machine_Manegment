using System.Net;
using System.Net.Sockets;
using HospitalPm.Api.Hosting;
using Npgsql;

namespace HospitalPm.IntegrationTests;

/// <summary>
/// The numbers for whoever runs the server. Off unless switched on; served on a port of their own; and nothing a
/// person typed ever becomes a label.
/// </summary>
[Collection(nameof(PostgresCollection))]
public sealed class MetricsTests(PostgresFixture fixture)
{
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task Requests_are_counted_by_route_pattern_on_a_port_of_their_own_and_never_by_the_address_typed()
    {
        var port = FreePort();
        using var factory = new ApiFactory(fixture.ConnectionString, new Dictionary<string, string?>
        {
            ["Metrics:Enabled"] = "true",
            ["Metrics:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        using var app = factory.CreateClient();

        // An equipment number, a made-up address and a search: none of them may turn up in the numbers.
        await app.GetAsync("/api/equipment/987654321");
        await app.GetAsync("/api/some/secret-thing-42?q=ventilator-for-bed-9");
        await app.GetAsync("/health");

        using var monitor = new HttpClient();
        var text = await monitor.GetStringAsync($"http://127.0.0.1:{port}/metrics");

        Assert.Contains("http_request_duration_seconds", text, StringComparison.Ordinal);
        Assert.Contains("route=\"/health\"", text, StringComparison.Ordinal);
        Assert.Contains("hospitalpm_database_connection_failures_total", text, StringComparison.Ordinal);
        Assert.Contains("route=\"/api/equipment/{id:int}\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("987654321", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-thing-42", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ventilator-for-bed-9", text, StringComparison.Ordinal);

        // The port people open does not hand the numbers out.
        var onTheAppPort = await (await app.GetAsync("/metrics")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("http_request_duration_seconds", onTheAppPort, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_listens_for_metrics_unless_it_is_switched_on()
    {
        var port = FreePort();
        using var factory = new ApiFactory(fixture.ConnectionString, new Dictionary<string, string?> { ["Metrics:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        using var app = factory.CreateClient();
        await app.GetAsync("/health");

        using var monitor = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => monitor.GetStringAsync($"http://127.0.0.1:{port}/metrics"));
    }

    [Fact]
    public void A_database_that_is_not_there_is_told_apart_from_one_that_is_slow_and_from_one_that_refused_a_save()
    {
        var refused = new NpgsqlException("down", new System.Net.Sockets.SocketException());
        Assert.True(DatabaseErrorMiddleware.IsConnectionFailure(refused));
        Assert.False(DatabaseErrorMiddleware.IsTimeout(refused));

        var dropped = new NpgsqlException("dropped", new System.IO.IOException("connection reset"));
        Assert.True(DatabaseErrorMiddleware.IsConnectionFailure(dropped));

        var slow = new NpgsqlException("slow", new TimeoutException());
        Assert.True(DatabaseErrorMiddleware.IsTimeout(slow));
        Assert.False(DatabaseErrorMiddleware.IsConnectionFailure(slow));

        // A request someone can fix is neither: it is counted as a refusal, under one of four fixed names.
        var duplicate = new PostgresException("dup", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation);
        Assert.False(DatabaseErrorMiddleware.IsConnectionFailure(duplicate));
        Assert.False(DatabaseErrorMiddleware.IsTimeout(duplicate));
        Assert.Equal("duplicate", DatabaseErrorMiddleware.ReasonOf(duplicate));
    }
}
