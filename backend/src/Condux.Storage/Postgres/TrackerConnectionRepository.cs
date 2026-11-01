using Condux.Core.Tickets;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// An org's grant to a tracker workspace (<c>tracker_connections</c>, ADR-0053). The renewal writes are
/// guarded on the refresh token the caller read, compared as the stored ciphertext: SecretBox seals with a
/// fresh nonce, so two seals of one token differ, but the bytes a caller read back are exactly the bytes
/// stored, which is what the guard needs. The row count is the answer, so no caller reads and then writes.
/// </summary>
public sealed class TrackerConnectionRepository(string connectionString) : ITrackerConnectionStore
{
    private const string Columns = """
        org_id, provider, workspace_id, workspace_name, access_token_encrypted, refresh_token_encrypted,
        access_expires_at, broken_at
        """;

    // Connecting again replaces the grant and clears a broken mark, since a fresh authorization is exactly
    // what a broken connection was waiting for.
    private const string UpsertSql = """
        INSERT INTO tracker_connections (org_id, provider, workspace_id, workspace_name,
            access_token_encrypted, refresh_token_encrypted, access_expires_at, connected_by)
        VALUES (@org, @provider, @workspace, @name, @access, @refresh, @expires, @by)
        ON CONFLICT (org_id, provider) DO UPDATE SET
            workspace_id = EXCLUDED.workspace_id, workspace_name = EXCLUDED.workspace_name,
            access_token_encrypted = EXCLUDED.access_token_encrypted,
            refresh_token_encrypted = EXCLUDED.refresh_token_encrypted,
            access_expires_at = EXCLUDED.access_expires_at, connected_by = EXCLUDED.connected_by,
            broken_at = NULL, updated_at = now();
        """;

    // A renewal that succeeded proves the grant works, so it also clears a broken mark: one process can be
    // refused a token another has already renewed and mark it broken first.
    private const string ReplaceTokensSql = """
        UPDATE tracker_connections
        SET access_token_encrypted = @access, refresh_token_encrypted = @refresh,
            access_expires_at = @expires, broken_at = NULL, updated_at = now()
        WHERE org_id = @org AND provider = @provider AND refresh_token_encrypted = @replaced;
        """;

    private const string MarkBrokenSql = """
        UPDATE tracker_connections SET broken_at = now(), updated_at = now()
        WHERE org_id = @org AND provider = @provider AND refresh_token_encrypted = @replaced
          AND broken_at IS NULL;
        """;

    public async Task<TrackerConnection?> GetAsync(long orgId, TicketProvider provider, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {Columns} FROM tracker_connections WHERE org_id = @org AND provider = @provider;", conn);
        AddKey(cmd, orgId, provider);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>The workspace the org's grant is for, and whether it is broken, with no token: for a process
    /// that reserves tickets and must never hold the grant itself.</summary>
    public async Task<(string WorkspaceId, bool Broken)?> GetWorkspaceAsync(
        long orgId, TicketProvider provider, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT workspace_id, broken_at IS NOT NULL FROM tracker_connections WHERE org_id = @org AND provider = @provider;", conn);
        AddKey(cmd, orgId, provider);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? (reader.GetString(0), reader.GetBoolean(1)) : null;
    }

    /// <summary>Store a fresh grant for the org, replacing any it had.</summary>
    public async Task UpsertAsync(TrackerConnection connection, long? connectedBy, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(UpsertSql, conn);
        AddKey(cmd, connection.OrgId, connection.Provider);
        cmd.Parameters.AddWithValue("workspace", connection.WorkspaceId);
        cmd.Parameters.AddWithValue("name", connection.WorkspaceName);
        cmd.Parameters.AddWithValue("access", connection.AccessTokenSealed);
        cmd.Parameters.AddWithValue("refresh", connection.RefreshTokenSealed);
        cmd.Parameters.AddWithValue("expires", connection.AccessExpiresAt);
        cmd.Parameters.AddWithValue("by", (object?)connectedBy ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ReplaceTokensAsync(
        long orgId, TicketProvider provider, byte[] replacedRefreshSealed, byte[] accessSealed,
        byte[] refreshSealed, DateTimeOffset accessExpiresAt, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ReplaceTokensSql, conn);
        AddKey(cmd, orgId, provider);
        cmd.Parameters.AddWithValue("replaced", replacedRefreshSealed);
        cmd.Parameters.AddWithValue("access", accessSealed);
        cmd.Parameters.AddWithValue("refresh", refreshSealed);
        cmd.Parameters.AddWithValue("expires", accessExpiresAt);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> MarkBrokenAsync(
        long orgId, TicketProvider provider, byte[] refusedRefreshSealed, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(MarkBrokenSql, conn);
        AddKey(cmd, orgId, provider);
        cmd.Parameters.AddWithValue("replaced", refusedRefreshSealed);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Remove the org's grant. False when there was none.</summary>
    public async Task<bool> DeleteAsync(long orgId, TicketProvider provider, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM tracker_connections WHERE org_id = @org AND provider = @provider;", conn);
        AddKey(cmd, orgId, provider);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static void AddKey(NpgsqlCommand cmd, long orgId, TicketProvider provider)
    {
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("provider", (short)provider);
    }

    private static TrackerConnection Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), (TicketProvider)r.GetInt16(1), r.GetString(2), r.GetString(3), (byte[])r[4], (byte[])r[5],
        r.GetFieldValue<DateTimeOffset>(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTimeOffset>(7));
}
