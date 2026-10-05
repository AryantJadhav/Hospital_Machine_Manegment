using HospitalPm.Infrastructure.Licensing;

namespace HospitalPm.Api.Hosting;

/// <summary>
/// Turns everyone away from a locked installation.
///
/// A lock is the supplier's instruction, in a signed code the hospital entered. While it stands nobody can sign
/// in and nothing opens: every request to the API is answered 423 with a reason, whoever it is from and whether
/// or not they were signed in a minute ago. The two things that still answer are the ones the lock screen needs: what
/// the lock says, and the box that takes an unlock code.
///
/// Placed before authentication, so it does not depend on who is asking, and so a token issued before the lock
/// is as useless as no token. The pages themselves are only a shell; they hold no records.
/// </summary>
public sealed class LicenceLockMiddleware(RequestDelegate next)
{
    private static readonly string[] Open =
    [
        "/api/licence/lock",
        "/api/licence/code",
    ];

    public async Task InvokeAsync(HttpContext context, LicenceService licences)
    {
        if (context.Request.Path.StartsWithSegments("/api")
            && !Open.Any(o => context.Request.Path.Equals(o, StringComparison.OrdinalIgnoreCase))
            && licences.CurrentLock() is { } state)
        {
            context.Response.StatusCode = StatusCodes.Status423Locked;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "This installation has been locked by your supplier. Contact them for an unlock code.",
                code = "licence-locked",
                hospitalName = state.HospitalName,
                licenceId = state.LicenceId,
            });
            return;
        }

        await next(context);
    }
}
