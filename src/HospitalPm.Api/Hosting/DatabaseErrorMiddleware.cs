using Npgsql;

namespace HospitalPm.Api.Hosting;

/// <summary>
/// Turns the four things the database refuses that are the caller's doing, not ours, from a server
/// error into a reply that says what to change.
///
/// Walking the screens with a script turned these up as bare 500s with no body: a 500-character asset
/// tag ("value too long"), a note containing a NUL character, a part cost of 1e20 ("numeric overflow"),
/// and, worst, two people saving the same thing at the same moment, where the loser of the race saw a
/// server error instead of "someone just did that". Each endpoint checks what it expects, and cannot be
/// expected to check every column's width, so the net is here once rather than in every handler.
///
/// A 5xx the person cannot act on is also a message that reads as "the software is broken". These are
/// not that, so they are not reported as that.
/// </summary>
public sealed class DatabaseErrorMiddleware(RequestDelegate next, ILogger<DatabaseErrorMiddleware> log)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (Describe(ex) is not null && !context.Response.HasStarted)
        {
            var (status, message) = Describe(ex)!.Value;

            // Logged at a level that does not page anyone: it is a refused request, not a fault.
            log.LogInformation(
                "{Method} {Path} was refused by the database ({State}): {Message}",
                context.Request.Method, context.Request.Path, StateOf(ex), message);

            context.Response.Clear();
            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new { error = message }, context.RequestAborted);
        }
    }

    /// <summary>The status and message for an error the caller can fix, or null for anything else.</summary>
    internal static (int Status, string Message)? Describe(Exception ex) => StateOf(ex) switch
    {
        PostgresErrorCodes.UniqueViolation =>
            (StatusCodes.Status409Conflict, "Someone saved the same thing a moment ago. Refresh the page and look again."),
        PostgresErrorCodes.StringDataRightTruncation =>
            (StatusCodes.Status400BadRequest, "One of the entries is too long. Shorten it and try again."),
        PostgresErrorCodes.NumericValueOutOfRange =>
            (StatusCodes.Status400BadRequest, "One of the numbers is too large. Check it and try again."),
        PostgresErrorCodes.UntranslatableCharacter or PostgresErrorCodes.CharacterNotInRepertoire =>
            (StatusCodes.Status400BadRequest, "One of the entries holds a character that cannot be saved. Retype it and try again."),
        _ => null,
    };

    private static string? StateOf(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is PostgresException pg)
            {
                return pg.SqlState;
            }
        }

        return null;
    }
}
