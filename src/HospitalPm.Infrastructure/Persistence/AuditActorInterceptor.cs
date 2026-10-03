using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace HospitalPm.Infrastructure.Persistence;

/// <summary>
/// Tells the database who is making the change, every time a connection is taken from the pool.
///
/// The audit log is written by database triggers, which cannot know who is signed in to the application:
/// they read it from a session setting, <c>app.user_id</c>. Until this existed nothing set it, so the
/// log recorded what changed and when but never by whom.
///
/// It is set on every connection open, not once, and set to nothing when there is no signed-in person
/// (the system's own jobs, a first-run setup). A pooled connection that last served someone else
/// therefore cannot lend its previous user to the next change.
/// </summary>
public sealed class AuditActorInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = Build(connection, eventData);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = Build(connection, eventData);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DbCommand Build(DbConnection connection, ConnectionEndEventData eventData)
    {
        var actor = (eventData.Context as HospitalPmDbContext)?.ActorUserId;

        var command = connection.CreateCommand();
        command.CommandText = "SELECT set_config('app.user_id', @actor, false)";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "actor";
        parameter.Value = actor?.ToString() ?? string.Empty;
        command.Parameters.Add(parameter);

        return command;
    }
}
