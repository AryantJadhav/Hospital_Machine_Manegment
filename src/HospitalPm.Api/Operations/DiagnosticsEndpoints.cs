using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using HospitalPm.Domain.Identity;
using HospitalPm.Infrastructure.Operations;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace HospitalPm.Api.Operations;

/// <summary>
/// Whether this installation is healthy, and how to reach it.
///
/// Admin only. The addresses are here because the first support question on
/// any install is "what do I type on the other machine", and answering it
/// from the server itself beats talking someone through ipconfig.
/// </summary>
public static class DiagnosticsEndpoints
{
    public static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/admin/diagnostics", RunAsync)
            .WithTags("Diagnostics")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }

    private static async Task<IResult> RunAsync(
        DiagnosticsService diagnostics,
        IServer server,
        IConfiguration configuration,
        CancellationToken ct)
    {
        var result = await diagnostics.RunAsync(Addresses(server), ct);

        return Results.Ok(new
        {
            // Collected by the installer. Shown here so support can confirm
            // which site they are looking at without asking.
            hospitalName = configuration["FirstRun:HospitalName"],
            overall = result.Overall,
            version = result.Version,
            uptimeSeconds = (long)result.Uptime.TotalSeconds,
            utcNow = result.UtcNow,
            addresses = result.Addresses,
            checks = result.Checks.Select(c => new
            {
                c.Name,
                c.State,
                c.Detail,
                c.Advice,
            }),
        });
    }

    /// <summary>
    /// The URLs another machine on the ward can actually use.
    ///
    /// Kestrel reports what it bound to, which is usually a wildcard like
    /// http://[::]:5000 — true, and useless to someone standing at a
    /// different PC. Wildcards are expanded into the machine's real IPv4
    /// addresses instead.
    /// </summary>
    private static List<string> Addresses(IServer server)
    {
        var bound = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (bound is null || bound.Count == 0) return [];

        var results = new List<string>();

        foreach (var address in bound)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var isWildcard = uri.Host is "0.0.0.0" or "[::]" or "::" or "*" or "+";

            if (!isWildcard)
            {
                results.Add(address);
                continue;
            }

            foreach (var ip in LocalIPv4())
            {
                results.Add($"{uri.Scheme}://{ip}:{uri.Port}");
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> LocalIPv4()
    {
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var info in nic.GetIPProperties().UnicastAddresses)
            {
                if (info.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(info.Address))
                {
                    yield return info.Address.ToString();
                }
            }
        }
    }
}
