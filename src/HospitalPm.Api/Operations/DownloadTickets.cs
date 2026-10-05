using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace HospitalPm.Api.Operations;

/// <summary>
/// A one-use, one-minute pass for a single file download.
///
/// A browser follows a plain link without the sign-in, and fetching a multi-gigabyte backup into the page's
/// memory to hand it back as a file would crash the tab. So the signed-in Developer asks for a pass, gets a
/// link carrying it, and the browser downloads the file itself, streaming straight to disk. The pass is
/// 256 random bits, works once, names one file chosen when it was issued, and is forgotten after a minute or
/// when the service restarts. Nothing is stored.
/// </summary>
public sealed class DownloadTickets(TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (string Path, string DownloadName, DateTime ExpiresUtc)> _tickets = new();

    public string Issue(string path, string downloadName)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var (key, ticket) in _tickets)
        {
            if (ticket.ExpiresUtc <= now)
            {
                _tickets.TryRemove(key, out _);
            }
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _tickets[token] = (path, downloadName, now + Lifetime);
        return token;
    }

    /// <summary>The file the pass is for, and gone for good, or null if the pass is unknown, used or too old.</summary>
    public (string Path, string DownloadName)? Redeem(string token)
    {
        if (!_tickets.TryRemove(token, out var ticket) || ticket.ExpiresUtc <= clock.GetUtcNow().UtcDateTime)
        {
            return null;
        }

        return (ticket.Path, ticket.DownloadName);
    }
}
