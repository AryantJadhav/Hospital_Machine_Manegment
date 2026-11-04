using System.Text.Json;
using HospitalPm.Api.Auth;
using HospitalPm.Domain.Identity;

namespace HospitalPm.Api.Notifications;

/// <summary>
/// The live stream behind the pop-up that tells the biomedical engineers and the head, at once, that a department has placed a
/// service request.
///
/// Server-sent events: one long-lived response that the program writes to when something happens. It needs no extra package and no
/// extra service, passes through the ordinary web server, and works on a hospital network with no internet. The browser reads it
/// with the same sign-in as every other call.
///
/// Only those who work on faults may listen: the engineers and the head. A department user is told about their own requests on
/// the request itself and in the list.
/// </summary>
public static class AlertEndpoints
{
    /// <summary>
    /// How long one stream stays open. A sign-in is only checked when a stream opens, so ending it now and then, and letting the
    /// browser open it again, is what makes a person who has been stopped, or had their access changed, fall off within minutes.
    /// </summary>
    private static readonly TimeSpan MaxLife = TimeSpan.FromMinutes(10);

    /// <summary>A line sent when nothing has happened, so a connection that has gone quiet is not mistaken for a dead one.</summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapAlertEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/notifications/stream", StreamAsync)
            .WithTags("Notifications")
            .RequireAnyPermission(Permissions.WorkOrdersWork, Permissions.WorkOrdersAssign);
    }

    private static async Task StreamAsync(HttpContext http, AlertHub hub, IHostApplicationLifetime lifetime)
    {
        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-transform";
        // Asks a proxy in front of the program not to hold the events back.
        response.Headers["X-Accel-Buffering"] = "no";

        // The person closing the page, the program shutting down, and the stream's own time are all the same: stop.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, lifetime.ApplicationStopping);
        limit.CancelAfter(MaxLife);
        var ct = limit.Token;

        using var subscription = hub.Subscribe();

        try
        {
            // The browser opens the stream again after a break, and this is how soon.
            await response.WriteAsync("retry: 3000\n\n", ct);
            await response.WriteAsync(": connected\n\n", ct);
            await response.Body.FlushAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(Heartbeat);

                try
                {
                    if (!await subscription.Reader.WaitToReadAsync(wait.Token))
                    {
                        return;
                    }

                    while (subscription.Reader.TryRead(out var alert))
                    {
                        await response.WriteAsync($"event: service-request\ndata: {JsonSerializer.Serialize(alert, Json)}\n\n", ct);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Nothing happened for a while: a comment line, which the browser ignores, keeps the connection alive.
                    await response.WriteAsync(": ping\n\n", ct);
                }

                await response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The page was closed, or the stream reached its time. Both are the normal end of it.
        }
        catch (IOException)
        {
            // The connection dropped under us.
        }
    }
}
