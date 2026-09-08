using System.Net;
using Microsoft.Extensions.Logging;

namespace HospitalPm.Infrastructure.Updates;

/// <summary>
/// Fetches an update file and the installer it names.
///
/// This is the only outbound HTTP in the product, and it took until now to
/// add one. Everything about it is shaped by that: it runs when an
/// administrator presses a button and at no other time, it sends nothing
/// about the hospital, and it is off unless a feed URL is configured.
///
/// It buys no trust. What comes back is verified by the same signature check
/// a USB stick goes through, and a download that fails verification is
/// deleted. Treat every byte here as hostile until then.
/// </summary>
public interface IUpdateDownloader
{
    /// <summary>Fetches a small text file — the signed manifest.</summary>
    Task<DownloadOutcome> GetTextAsync(Uri url, int maxBytes, CancellationToken ct);

    /// <summary>
    /// Streams a large file to disk, refusing to write more than maxBytes.
    /// The destination is deleted if anything goes wrong, so a partial file
    /// is never left looking like a real one.
    /// </summary>
    Task<DownloadOutcome> GetFileAsync(Uri url, string destination, long maxBytes, CancellationToken ct);
}

/// <param name="Ok">Whether the fetch completed.</param>
/// <param name="Text">The body, for text fetches only.</param>
/// <param name="Problem">Plain English, for someone who is not a developer.</param>
public sealed record DownloadOutcome(bool Ok, string? Text, string? Problem)
{
    public static DownloadOutcome Failed(string problem) => new(false, null, problem);

    public static DownloadOutcome Succeeded(string? text = null) => new(true, text, null);
}

/// <remarks>
/// Takes an HttpClient rather than an IHttpClientFactory so this project
/// needs no new package: Microsoft.Extensions.Http lives in the ASP.NET
/// Core shared framework, which the API project already references, and
/// adding it here would be a NuGet dependency bought for one constructor
/// parameter. The timeout, redirect and cookie policy is set where the
/// client is registered.
/// </remarks>
public sealed partial class UpdateDownloader(
    HttpClient http,
    ILogger<UpdateDownloader> logger) : IUpdateDownloader
{
    public async Task<DownloadOutcome> GetTextAsync(Uri url, int maxBytes, CancellationToken ct)
    {
        if (Reject(url) is { } refusal) return refusal;

        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            if (WrongPlace(response) is { } moved) return moved;
            if (!response.IsSuccessStatusCode) return HttpProblem(response, url);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[maxBytes + 1];
            var read = await ReadAtMostAsync(stream, buffer, ct);

            if (read > maxBytes)
            {
                return DownloadOutcome.Failed(
                    "The update file at that address is far larger than an update file should be. "
                    + "Check the address is right.");
            }

            return DownloadOutcome.Succeeded(System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.FetchFailed(logger, url.ToString(), e);
            return DownloadOutcome.Failed(Describe(e));
        }
    }

    public async Task<DownloadOutcome> GetFileAsync(
        Uri url, string destination, long maxBytes, CancellationToken ct)
    {
        if (Reject(url) is { } refusal) return refusal;

        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            if (WrongPlace(response) is { } moved) return moved;
            if (!response.IsSuccessStatusCode) return HttpProblem(response, url);

            // Checked before a byte is written where the server declares it,
            // so an obviously wrong download costs nothing. The running count
            // below is what actually enforces it — Content-Length is the
            // server's claim, not a fact.
            if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
            {
                return TooLarge(maxBytes);
            }

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 1024 * 128, useAsync: true))
            {
                var buffer = new byte[1024 * 128];
                long written = 0;

                while (true)
                {
                    var read = await source.ReadAsync(buffer, ct);
                    if (read == 0) break;

                    written += read;
                    if (written > maxBytes)
                    {
                        // Abandoned mid-stream rather than after the fact. The
                        // point of a cap is that the disk never fills.
                        file.Close();
                        Delete(destination);
                        return TooLarge(maxBytes);
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            return DownloadOutcome.Succeeded();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException
                                      or UnauthorizedAccessException)
        {
            Log.FetchFailed(logger, url.ToString(), e);

            // A half-written installer is worse than none: it would sit in the
            // folder looking like a real one until somebody hashed it.
            Delete(destination);
            return DownloadOutcome.Failed(Describe(e));
        }
    }

    /// <summary>
    /// HTTPS and nothing else, before a socket is opened.
    ///
    /// The signature is what makes a downloaded installer safe to run, so
    /// plaintext would not actually be dangerous. It is refused anyway
    /// because there is no reason to accept one: a hospital that cannot do
    /// HTTPS has the USB stick path, which is what it is for.
    /// </summary>
    private static DownloadOutcome? Reject(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps
            ? null
            : DownloadOutcome.Failed(
                $"Updates can only be fetched over https, and this address is {url.Scheme}. "
                + "Correct the address, or copy the update across on a USB stick instead.");

    /// <summary>
    /// A redirect that ended up somewhere other than https.
    ///
    /// The handler follows redirects because release hosting relies on them,
    /// which means the final address is chosen by the server rather than by
    /// us. Downgrading to plaintext along the way is the one move worth
    /// refusing outright.
    /// </summary>
    private static DownloadOutcome? WrongPlace(HttpResponseMessage response) =>
        response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps
            ? DownloadOutcome.Failed(
                "That address redirected to an insecure one. Nothing was downloaded.")
            : null;

    private static DownloadOutcome TooLarge(long maxBytes) =>
        DownloadOutcome.Failed(
            $"The download is larger than the {maxBytes / 1024 / 1024} MB limit and was stopped. "
            + "Nothing has been installed.");

    private static DownloadOutcome HttpProblem(HttpResponseMessage response, Uri url) =>
        DownloadOutcome.Failed(response.StatusCode switch
        {
            HttpStatusCode.NotFound =>
                $"There is nothing at {url}. Either no update has been published yet, or the address is wrong.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                "That address refused the request. If this network uses a proxy that asks for a sign-in, "
                + "updates will have to be copied across on a USB stick instead.",
            _ => $"The server answered {(int)response.StatusCode}. Nothing was downloaded.",
        });

    /// <summary>
    /// Turns a network exception into something a hospital IT contact can act
    /// on. "No such host is known" means the machine has no internet far more
    /// often than it means the address is wrong, and saying so saves an hour.
    /// </summary>
    private static string Describe(Exception e) => e switch
    {
        TaskCanceledException =>
            "The download timed out. This machine may have no route to the internet, or the connection "
            + "may be too slow to finish. Copying the update across on a USB stick always works.",
        HttpRequestException { InnerException: System.Net.Sockets.SocketException } =>
            "Could not reach that address. This machine probably has no internet — which is normal for a "
            + "hospital PC. Copy the update across on a USB stick instead.",

        // Worth its own message rather than the general one, which reads
        // "The SSL connection could not be established, see inner
        // exception" — true, and of no use to anyone standing at a
        // hospital PC. Seen for real against a self-signed test server.
        HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } =>
            "The secure connection to that address could not be trusted. Its certificate may have "
            + "expired, or something on this network may be inspecting traffic. Nothing was "
            + "downloaded — copy the update across on a USB stick instead.",

        HttpRequestException http =>
            $"The download failed: {http.Message}",
        _ => "The download failed and nothing was installed.",
    };

    /// <summary>
    /// Reads until the buffer is full or the stream ends. A single ReadAsync
    /// is allowed to return fewer bytes than asked for, which on a slow
    /// connection silently truncates a manifest into an unreadable one.
    /// </summary>
    private static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.DeleteFailed(logger, path, e);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Could not fetch {Url}")]
        public static partial void FetchFailed(ILogger logger, string url, Exception e);

        [LoggerMessage(Level = LogLevel.Information, Message = "Could not delete {Path}")]
        public static partial void DeleteFailed(ILogger logger, string path, Exception e);
    }
}
