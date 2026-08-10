using Condux.Core.SourceMaps;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// Source-map artifact index in Postgres (ADR-0028). The map bytes live in object storage; this row
/// indexes them by debugId + (release, dist, filename) for read-time symbolication. Re-uploading the same
/// artifact (same object key) updates the row rather than duplicating it.
/// </summary>
public sealed class SourceMapArtifactRepository(string connectionString)
{
    private const string Columns =
        "id, project_id, release, dist, debug_id, filename, object_key, byte_size, created_at";

    private const string RecordSql = $"""
        INSERT INTO sourcemap_artifacts (id, project_id, release, dist, debug_id, filename, object_key, byte_size)
        VALUES (@id, @project, @release, @dist, @debug, @filename, @key, @size)
        ON CONFLICT (project_id, object_key) DO UPDATE
          SET release = EXCLUDED.release, dist = EXCLUDED.dist, debug_id = EXCLUDED.debug_id,
              filename = EXCLUDED.filename, byte_size = EXCLUDED.byte_size
        RETURNING {Columns};
        """;

    // Resolution for symbolication (ADR-0028): debug id first (robust), then (release, filename) fallback.
    // Both take the most recent match, so a re-upload wins.
    private const string FindByDebugIdSql = $"""
        SELECT {Columns} FROM sourcemap_artifacts
        WHERE project_id = @project AND debug_id = @debug ORDER BY created_at DESC LIMIT 1;
        """;

    private const string FindByReleaseFileSql = $"""
        SELECT {Columns} FROM sourcemap_artifacts
        WHERE project_id = @project AND release = @release AND filename = @filename
        ORDER BY created_at DESC LIMIT 1;
        """;

    public async Task<SourceMapArtifact> RecordAsync(
        long projectId, string release, string? dist, string? debugId, string filename, string objectKey,
        long byteSize, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(RecordSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("release", release);
        cmd.Parameters.AddWithValue("dist", (object?)dist ?? DBNull.Value);
        cmd.Parameters.AddWithValue("debug", (object?)debugId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("filename", filename);
        cmd.Parameters.AddWithValue("key", objectKey);
        cmd.Parameters.AddWithValue("size", byteSize);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return Map(reader);
    }

    public async Task<SourceMapArtifact?> FindByDebugIdAsync(
        long projectId, string debugId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(FindByDebugIdSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("debug", debugId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<SourceMapArtifact?> FindByReleaseFileAsync(
        long projectId, string release, string filename, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(FindByReleaseFileSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("release", release);
        cmd.Parameters.AddWithValue("filename", filename);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    private static SourceMapArtifact Map(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetInt64(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.GetString(5), r.GetString(6), r.GetInt64(7), r.GetFieldValue<DateTimeOffset>(8));
}
