namespace HospitalPm.Api.Hosting;

/// <summary>
/// Advertises this machine as "hospitalpm.local" (or a configured name) via
/// mDNS, so phones and browsers on the ward network can reach the system
/// without knowing the IP address.
///
/// This is the feature the build plan calls out as: "mDNS so devices resolve
/// hospitalpm.local and a DHCP change never becomes a support call."
///
/// Runs as a hosted service: starts after the app is serving, stops on
/// shutdown. Fails gracefully — if multicast is unavailable (no NIC, port
/// conflict with another mDNS responder), the app still works normally and
/// the hospital just uses the IP address.
/// </summary>
public sealed class MdnsAdvertiser : IHostedService, IDisposable
{
    private readonly IConfiguration _config;
    private readonly ILogger<MdnsAdvertiser> _logger;
    private MdnsResponder? _responder;

    public MdnsAdvertiser(IConfiguration config, ILogger<MdnsAdvertiser> logger)
    {
        _config = config;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var enabled = _config.GetValue("Mdns:Enabled", true);
        if (!enabled)
        {
            _logger.LogInformation("mDNS advertisement disabled by configuration");
            return Task.CompletedTask;
        }

        var hostname = _config.GetValue("Mdns:Hostname", "hospitalpm") ?? "hospitalpm";

        _responder = new MdnsResponder(hostname, _logger);
        _responder.Start();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _responder?.Dispose();
        _responder = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _responder?.Dispose();
    }
}
