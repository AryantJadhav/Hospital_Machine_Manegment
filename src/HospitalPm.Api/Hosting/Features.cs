using Microsoft.Extensions.Options;

namespace HospitalPm.Api.Hosting;

/// <summary>
/// Parts of the product that can be switched off for everyone, Administrators included.
///
/// Off by default here, and set in appsettings.json under "Features". A part that is off is not just
/// hidden from the menu: its routes answer as if they did not exist, so nobody can reach it by typing
/// the address or calling the API. Switching one back on is a setting and a restart, not a change of code.
///
/// The nightly backup is not one of these. It runs on its own whatever is set here; "Backups" is only the
/// page, the run-now button and restoring from a backup.
/// </summary>
public sealed class FeatureOptions
{
    public const string SectionName = "Features";

    /// <summary>Loading machines and locations from an Excel file.</summary>
    public bool Import { get; set; }

    /// <summary>Downloading everything as a zip of ordinary files.</summary>
    public bool Export { get; set; }

    /// <summary>The Backups page, taking one now, and restoring from one.</summary>
    public bool Backups { get; set; }

    /// <summary>Checking for and installing a new version.</summary>
    public bool Updates { get; set; }
}

/// <summary>
/// Refuses requests to a part that is switched off.
///
/// A middleware, and placed after sign-in has been checked but before any endpoint runs, so it does not
/// depend on the request being well formed: an endpoint filter runs only after the body has been read, and
/// a call with no body would be told it was a bad request instead of that the part is off. Somebody who is
/// not signed in is still asked to sign in first, and is not told what is switched off.
/// </summary>
public sealed class FeatureSwitchMiddleware(RequestDelegate next, IOptionsMonitor<FeatureOptions> options)
{
    private static readonly (string Prefix, Func<FeatureOptions, bool> IsOn, string Name)[] Parts =
    [
        ("/api/equipment/import", o => o.Import, "Import"),
        ("/api/admin/export", o => o.Export, "Export"),
        // The page, running one now, and restoring from one, which lives under it.
        ("/api/admin/backups", o => o.Backups, "Backups"),
        ("/api/admin/update", o => o.Updates, "Updates"),
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        foreach (var (prefix, isOn, name) in Parts)
        {
            if (context.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase)
                && !isOn(options.CurrentValue))
            {
                // Not found, not forbidden: to the caller it is not there.
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                await context.Response.WriteAsJsonAsync(new { error = $"{name} is switched off." });
                return;
            }
        }

        await next(context);
    }
}

public static class Features
{
    /// <summary>What is on, so the screens show only what works. Anyone signed in may ask.</summary>
    public static void MapFeatureEndpoints(this IEndpointRouteBuilder app)
        => app.MapGet("/api/features", (IOptionsMonitor<FeatureOptions> options) =>
        {
            var o = options.CurrentValue;
            return Results.Ok(new { import = o.Import, export = o.Export, backups = o.Backups, updates = o.Updates });
        })
            .WithTags("Features")
            .RequireAuthorization();
}
