using Condux.Core.OrgNotifications;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// Postgres-backed <see cref="IPauseNotifyThrottle"/> (#130): one <c>org_pause_notifications</c> row per
/// (org, reason). A single atomic upsert both records the send and decides whether to send — the guarded
/// <c>DO UPDATE ... WHERE</c> only fires (and only then RETURNs a row) when the last notice is older than
/// the window, so concurrent consumers can't double-notify.
/// </summary>
public sealed class PostgresPauseNotifyThrottle(string connectionString) : IPauseNotifyThrottle
{
    // First notice (no row) inserts and returns. A repeat updates+returns only when the previous send is
    // older than the cutoff; inside the window the WHERE is false, nothing updates, and RETURNING is empty.
    private const string Sql = """
        INSERT INTO org_pause_notifications (org_id, reason, notified_at)
        VALUES (@org, @reason, @now)
        ON CONFLICT (org_id, reason) DO UPDATE SET notified_at = @now
        WHERE org_pause_notifications.notified_at < @cutoff
        RETURNING notified_at;
        """;

    public async Task<bool> TryAcquireAsync(
        long orgId, ConductorPauseReason reason, DateTimeOffset now, TimeSpan window,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(Sql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("reason", (short)reason);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("cutoff", now - window);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }
}
