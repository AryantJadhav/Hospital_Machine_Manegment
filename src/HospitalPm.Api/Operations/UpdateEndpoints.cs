using HospitalPm.Domain.Identity;
using HospitalPm.Domain.Updates;
using HospitalPm.Infrastructure.Updates;
using Microsoft.AspNetCore.Mvc;

namespace HospitalPm.Api.Operations;

/// <summary>
/// Installing an update from a file, for a machine with no internet.
///
/// Administrators only, and the reason is stronger than for the other admin
/// routes: this one ends with an executable running as LocalSystem. The
/// signature check is the real gate — an Admin cannot install an unsigned
/// update either — but there is no reason for the route to be reachable by
/// anyone else.
/// </summary>
public static partial class UpdateEndpoints
{
    public static void MapUpdateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/admin/update")
            .WithTags("Update")
            .RequireAuthorization(p => p.RequireRole(Roles.Admin));

        group.MapGet("/", Status);
        group.MapPost("/check", CheckOnlineAsync);
        group.MapPost("/install", InstallAsync);
    }

    /// <summary>
    /// What is running, and what is sitting in a folder waiting to be
    /// installed. The folder is a query parameter so someone can point this at
    /// a USB stick — "E:\" — without copying anything first.
    /// </summary>
    private static IResult Status(UpdateService updates, [FromQuery] string? folder)
    {
        var scanned = string.IsNullOrWhiteSpace(folder) ? updates.DefaultFolder : folder.Trim();

        return Results.Ok(new
        {
            enabled = updates.Enabled,
            canCheckOnline = updates.CanCheckOnline,
            feedUrl = updates.FeedUrl,
            runningVersion = UpdateService.RunningVersion.ToString(3),
            defaultFolder = updates.DefaultFolder,
            folder = scanned,
            available = updates.Scan(folder).Select(Describe),
        });
    }

    /// <summary>
    /// Looks online for a newer release, and optionally downloads it into
    /// the update folder.
    ///
    /// Nothing is installed here. A successful download leaves exactly what
    /// a USB stick would have left - a signed manifest and the installer it
    /// names, in the same folder - and the ordinary install route takes it
    /// from there. The download is a delivery mechanism, not a shortcut
    /// past any check.
    /// </summary>
    private static async Task<IResult> CheckOnlineAsync(
        [FromBody] CheckUpdateRequest request,
        UpdateService updates,
        CancellationToken ct)
    {
        var result = await updates.CheckOnlineAsync(request.Download, ct);

        // 200 even when nothing was reachable. "This machine has no
        // internet" is the expected answer on most hospital PCs, not a
        // failure of the request, and a page that renders it as an error
        // teaches people that the button is broken.
        return Results.Ok(new
        {
            reachable = result.Reachable,
            problem = result.Problem,
            available = result.Candidate is null ? null : Describe(result.Candidate),
        });
    }

    /// <summary>
    /// Verifies once more, backs up, and hands over to the installer.
    ///
    /// Answers 202 rather than 200 because nothing here is finished when the
    /// response is written: the service is about to be stopped by the process
    /// it just started. The page's job after this is to wait for the server to
    /// come back on a new version, not to believe a success message.
    /// </summary>
    private static async Task<IResult> InstallAsync(
        [FromBody] InstallUpdateRequest request,
        UpdateService updates,
        ILogger<InstallUpdateRequest> logger,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ManifestPath))
        {
            return Results.BadRequest(new { error = "Say which update file to install." });
        }

        var result = await updates.InstallAsync(request.ManifestPath.Trim(), ct);

        if (!result.Started)
        {
            // 409 and not 400: the request was well formed and the refusal is
            // about the state of the files on disk, which is what the message
            // explains.
            return Results.Conflict(new { error = result.Message });
        }

        HandedOver(logger, result.Version!);

        return Results.Accepted(value: new
        {
            version = result.Version,
            logPath = result.LogPath,
            message =
                $"Installing version {result.Version}. The service will stop and start again on its own; "
                + "this page will reconnect when it is back. Do not turn the machine off.",
        });
    }

    private static object Describe(UpdateCandidate candidate) => new
    {
        state = candidate.State.ToString(),
        canInstall = candidate.CanInstall,
        manifestPath = candidate.ManifestPath,
        fileName = Path.GetFileName(candidate.ManifestPath),
        message = candidate.Message,
        version = candidate.Manifest?.Version,
        releasedOn = candidate.Manifest?.ReleasedOn,
        notes = candidate.Manifest?.Notes,
        sizeBytes = candidate.Manifest?.SizeBytes,
        installerFileName = candidate.Manifest?.InstallerFileName,
    };

    internal sealed record InstallUpdateRequest(string ManifestPath);

    /// <param name="Download">
    /// False only looks, which is a few hundred bytes and answers "is there
    /// anything new" without committing a poor connection to a couple of
    /// hundred megabytes.
    /// </param>
    internal sealed record CheckUpdateRequest(bool Download);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Update to {Version} handed over to the installer")]
    private static partial void HandedOver(ILogger logger, string version);
}
