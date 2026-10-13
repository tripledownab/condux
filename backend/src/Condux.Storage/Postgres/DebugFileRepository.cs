using Condux.Core.DebugFiles;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// The native debug file index in Postgres. The file bytes live in object storage; this row says which
/// build they belong to. One row per project and build: uploading the same build again replaces the row
/// rather than adding a second, so a CI job that runs twice leaves one answer.
/// </summary>
public sealed class DebugFileRepository(string connectionString)
{
    private const string Columns = "id, project_id, build_id, debug_id, object_key, byte_size, created_at, sha256";

    // An upload again counts as the newest, since that is the build a crash after it most likely came from.
    private const string RecordSql = $"""
        INSERT INTO debug_files (id, project_id, build_id, debug_id, object_key, byte_size, sha256)
        VALUES (@id, @project, @build, @debug, @key, @size, @sha)
        ON CONFLICT (project_id, build_id) DO UPDATE
          SET debug_id = EXCLUDED.debug_id, object_key = EXCLUDED.object_key, byte_size = EXCLUDED.byte_size,
              sha256 = EXCLUDED.sha256, created_at = now()
        RETURNING {Columns};
        """;

    // One row per debug id. Two builds share one when their build IDs agree in the first 16 bytes once a
    // shorter one is padded with zeros; the newest upload is the one a fresh crash came from.
    private const string FindSql = $"""
        SELECT DISTINCT ON (debug_id) {Columns}
        FROM debug_files
        WHERE project_id = @project AND debug_id = ANY(@debug)
        ORDER BY debug_id, created_at DESC;
        """;

    // A compare-and-set on the hash the read saw, absent included, so a read never overwrites a hash written
    // after it looked, whether by an upload or by another read.
    private const string CorrectHashSql =
        "UPDATE debug_files SET sha256 = @sha WHERE id = @id AND sha256 IS NOT DISTINCT FROM @seen;";

    private const string RecordStoredHashSql = "UPDATE debug_files SET sha256 = @sha WHERE id = @id;";

    public async Task<DebugFile> RecordAsync(
        long projectId, ElfBuild build, string objectKey, long byteSize, string? sha256,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        await ObjectKeyLocks.LockAsync(conn, tx, objectKey, cancellationToken);
        await using var cmd = new NpgsqlCommand(RecordSql, conn, tx);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("build", build.BuildId);
        cmd.Parameters.AddWithValue("debug", build.DebugId);
        cmd.Parameters.AddWithValue("key", objectKey);
        cmd.Parameters.AddWithValue("size", byteSize);
        cmd.Parameters.AddWithValue("sha", (object?)sha256 ?? DBNull.Value);
        DebugFile file;
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            file = Read(reader);
        }
        await tx.CommitAsync(cancellationToken);
        return file;
    }

    /// <summary>The project's files for these debug ids, keyed by debug id, in one query.</summary>
    public async Task<IReadOnlyDictionary<string, DebugFile>> FindAsync(
        long projectId, IReadOnlyCollection<string> debugIds, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, DebugFile>(StringComparer.Ordinal);
        if (debugIds.Count == 0)
        {
            return found;
        }
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(FindSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("debug", debugIds.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var file = Read(reader);
            found[file.DebugId] = file;
        }
        return found;
    }

    /// <summary>Records the hash a read computed from the bytes it read, if the row still holds the hash the read
    /// saw (<paramref name="seen"/>, null for none). That fills a row with no hash, one uploaded before hashes
    /// were recorded or read while its upload was storing the bytes, and corrects one whose upload recorded
    /// the hash of bytes another upload of the same build then replaced.</summary>
    public Task CorrectSha256Async(
        Guid id, string? seen, string sha256, CancellationToken cancellationToken = default) =>
        SetHashAsync(CorrectHashSql, id, sha256, cancellationToken,
            new NpgsqlParameter("seen", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)seen ?? DBNull.Value });

    /// <summary>Records the hash of the bytes an upload has just stored, over whatever the row holds: a read
    /// during the upload may have hashed the object those bytes replaced and written that first.</summary>
    public Task RecordStoredSha256Async(Guid id, string sha256, CancellationToken cancellationToken = default) =>
        SetHashAsync(RecordStoredHashSql, id, sha256, cancellationToken);

    private async Task SetHashAsync(
        string sql, Guid id, string sha256, CancellationToken cancellationToken, params NpgsqlParameter[] more)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("sha", sha256);
        cmd.Parameters.AddRange(more);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DebugFile Read(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetInt64(5), reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetString(7));
}
