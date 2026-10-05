using HospitalPm.Infrastructure.Licensing;

namespace HospitalPm.Api.Hosting;

/// <summary>
/// Refuses anything that records something new once the licence has expired and
/// its grace period has ended.
///
/// Reading is never refused: the ventilator's service history must open at two
/// in the morning whatever the state of the paperwork. Neither are the things a
/// hospital needs in order to fix the situation, or to stay safe while it does.
/// </summary>
public sealed class LicenceReadOnlyMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Writes that still go through. Signing in is not "recording"; the licence
    /// page is how a renewal is installed; backups and updates protect the data;
    /// and first-time setup happens before there is a licence at all.
    /// </summary>
    private static readonly string[] Allowed =
    [
        "/api/auth/",
        "/api/setup/",
        "/api/admin/licence",
        // A code is entered where there may be no one signed in, and the Developer's licence section is not recording.
        "/api/licence/",
        "/api/developer/licences",
        "/api/admin/backups",
        "/api/admin/update",
    ];

    public async Task InvokeAsync(HttpContext context, LicenceService licences)
    {
        if (IsWrite(context.Request.Method) && NeedsLicence(context.Request.Path))
        {
            var status = licences.Current();

            if (status.IsReadOnly)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = status.Message,
                    code = "licence-read-only",
                });
                return;
            }
        }

        await next(context);
    }

    private static bool IsWrite(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    private static bool NeedsLicence(PathString path) =>
        path.StartsWithSegments("/api")
        && !Allowed.Any(a => path.Value!.StartsWith(a, StringComparison.OrdinalIgnoreCase));
}
