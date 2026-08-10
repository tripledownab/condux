using Condux.Core.Alerting;
using Condux.Core.OrgNotifications;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>Postgres-backed store for an org's notification channels (#129) in
/// <c>org_notification_channels</c>. Simple per-org CRUD; delivery lives in Condux.Notifications.</summary>
public sealed class OrgNotificationChannelRepository(string connectionString)
{
    public async Task<IReadOnlyList<OrgNotificationChannel>> ListByOrgAsync(
        long orgId, CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT id, org_id, channel, target, created_at
            FROM org_notification_channels
            WHERE org_id = @org
            ORDER BY created_at;
            """;
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        var list = new List<OrgNotificationChannel>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new OrgNotificationChannel(
                reader.GetGuid(0), reader.GetInt64(1), (NotificationChannel)reader.GetInt16(2),
                reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4)));
        }
        return list;
    }

    public async Task<OrgNotificationChannel> AddAsync(
        long orgId, NotificationChannel channel, string target, CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO org_notification_channels (id, org_id, channel, target, created_at)
            VALUES (@id, @org, @channel, @target, @created);
            """;
        var id = Guid.CreateVersion7();
        var createdAt = DateTimeOffset.UtcNow;
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("channel", (short)channel);
        cmd.Parameters.AddWithValue("target", target);
        cmd.Parameters.AddWithValue("created", createdAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return new OrgNotificationChannel(id, orgId, channel, target, createdAt);
    }

    /// <summary>Delete one channel scoped to its org (tenancy-safe). True when a row was removed.</summary>
    public async Task<bool> DeleteAsync(long orgId, Guid id, CancellationToken cancellationToken = default)
    {
        const string sql = "DELETE FROM org_notification_channels WHERE org_id = @org AND id = @id;";
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
