using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A DSN public key for a project. Only <c>IsActive</c> keys authenticate ingest.</summary>
public sealed record DsnKey(
    long Id, long ProjectId, string PublicKey, string Label, bool IsActive,
    DateTimeOffset CreatedAt, DateTimeOffset? RevokedAt);

/// <summary>Creates, lists, and revokes DSN keys (supports rotation).</summary>
public sealed class DsnKeyRepository(string connectionString)
{
    private const string InsertSql = """
        INSERT INTO dsn_keys (project_id, public_key, label)
        VALUES (@project, @key, @label)
        RETURNING id, project_id, public_key, label, is_active, created_at, revoked_at;
        """;

    private const string ListSql = """
        SELECT id, project_id, public_key, label, is_active, created_at, revoked_at
        FROM dsn_keys WHERE project_id = @project ORDER BY id;
        """;

    private const string RevokeSql =
        "UPDATE dsn_keys SET is_active = false, revoked_at = now() " +
        "WHERE id = @id AND project_id = @project AND is_active;";

    private const string RenameSql = """
        UPDATE dsn_keys SET label = @label
        WHERE id = @id AND project_id = @project
        RETURNING id, project_id, public_key, label, is_active, created_at, revoked_at;
        """;

    public async Task<DsnKey> CreateAsync(
        long projectId, string label, string publicKey, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("key", publicKey);
        cmd.Parameters.AddWithValue("label", label);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    public async Task<IReadOnlyList<DsnKey>> ListByProjectAsync(long projectId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ListSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);

        var list = new List<DsnKey>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    /// <summary>
    /// Revoke a key within its project (tenant-scoped, so a key can't be revoked by id across
    /// projects); returns false if it doesn't exist for that project or was already revoked.
    /// </summary>
    public async Task<bool> RevokeAsync(long projectId, long keyId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(RevokeSql, conn);
        cmd.Parameters.AddWithValue("id", keyId);
        cmd.Parameters.AddWithValue("project", projectId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Rename a key within its project (tenant-scoped, so a key can't be renamed by id across projects);
    /// returns null if it doesn't exist for that project. The label is cosmetic, so a revoked key renames too.
    /// </summary>
    public async Task<DsnKey?> UpdateLabelAsync(
        long projectId, long keyId, string label, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(RenameSql, conn);
        cmd.Parameters.AddWithValue("id", keyId);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("label", label);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    private static DsnKey Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetBoolean(4),
        r.GetFieldValue<DateTimeOffset>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTimeOffset>(6));
}
