using Prometheus;

namespace HospitalPm.Api.Hosting;

/// <summary>
/// Numbers for whoever runs the server (Prometheus and Grafana): how many requests, how fast, how many failed, and how
/// often the database could not be reached. It is for the people who run Hospital PM for a hospital, not for the
/// hospital's staff, and it is OFF unless <c>Metrics:Enabled</c> is true.
///
/// Two rules keep it harmless:
///  - The numbers are served on a port of their own (<c>Metrics:Port</c>, 9102), never on the one people open in a
///    browser, so an address that is open to the hospital's network does not also hand out figures about the server.
///    Whoever switches it on must also keep that port to the monitoring service alone (a network policy, or a firewall).
///  - Nothing a person typed or owns becomes a label. Requests are counted by the route's pattern
///    (<c>/api/equipment/{id:int}</c>), never by the address as it was sent, so an equipment number, a search or a user
///    name cannot appear, and the number of labels cannot grow with the data. There is no patient data in the product
///    to leak, and this keeps it so.
/// </summary>
public static class ServerMetrics
{
    /// <summary>Requests that failed because the database could not be reached, or did not answer in time.</summary>
    public static readonly Counter DatabaseConnectionFailures = Metrics.CreateCounter(
        "hospitalpm_database_connection_failures_total",
        "Requests that failed because the database could not be reached.");

    /// <summary>Requests that failed because the database took too long to answer.</summary>
    public static readonly Counter DatabaseTimeouts = Metrics.CreateCounter(
        "hospitalpm_database_timeouts_total",
        "Requests that failed because the database took too long to answer.");

    /// <summary>Requests the database refused for something the person can change (a duplicate, a value too long). Not faults.</summary>
    public static readonly Counter DatabaseRefusals = Metrics.CreateCounter(
        "hospitalpm_database_refusals_total",
        "Saves the database refused for something the caller can fix.",
        new CounterConfiguration { LabelNames = ["reason"] });

    public static bool IsEnabled(IConfiguration configuration) => configuration.GetValue("Metrics:Enabled", false);

    /// <summary>
    /// Switches the request metrics on and starts the separate listener. Does nothing unless enabled. Call before the
    /// rest of the pipeline, so a request turned away early (a locked installation) is counted too.
    /// </summary>
    public static void Use(WebApplication app)
    {
        if (!IsEnabled(app.Configuration))
        {
            return;
        }

        // Touching them makes them exist, so a figure that is still zero is shown as zero, not missing: an alert on
        // "the database could not be reached" needs the series to be there before the first failure.
        _ = DatabaseConnectionFailures;
        _ = DatabaseTimeouts;

        app.UseHttpMetrics(options =>
        {
            // The pattern the request matched. A request that matched nothing (a file, or a missing address) is "none",
            // a single value, so a scanner asking for a thousand different paths adds one label, not a thousand.
            options.AddCustomLabel("route", context => RoutePattern(context));
        });

        var port = app.Configuration.GetValue("Metrics:Port", 9102);
        var server = new KestrelMetricServer(port);
        server.Start();
        app.Lifetime.ApplicationStopping.Register(() => server.Stop());
        app.Logger.LogInformation("Metrics are served on port {Port}, separate from the application's own.", port);
    }

    internal static string RoutePattern(HttpContext context) =>
        (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "none";
}
