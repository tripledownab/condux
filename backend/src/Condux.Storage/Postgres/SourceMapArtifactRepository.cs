using System.Collections.ObjectModel;
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
    // Both take the most recent match, so a re-upload wins, which is what DISTINCT ON keeps here.
    //
    // Batched on purpose: the keys come from an event's frame list, which is written by whoever sent the
    // event and has no length the sender cannot choose. A per-key round trip turned one dashboard read
    // into as many queries as the event had frames, so the caller decided how long the control plane
    // held a connection. One query per event, whatever the frames say.
    private const string FindByDebugIdsSql = $"""
        SELECT DISTINCT ON (debug_id) {Columns} FROM sourcemap_artifacts
        WHERE project_id = @project AND debug_id = ANY(@debug)
        ORDER BY debug_id, created_at DESC;
        """;

    private const string FindByReleaseFilesSql = $"""
        SELECT DISTINCT ON (filename) {Columns} FROM sourcemap_artifacts
        WHERE project_id = @project AND release = @release AND filename = ANY(@filenames)
        ORDER BY filename, created_at DESC;
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

    /// <summary>The newest artifact for each of <paramref name="debugIds"/> that has one, keyed by debug id.</summary>
    public async Task<IReadOnlyDictionary<string, SourceMapArtifact>> FindByDebugIdsAsync(
        long projectId, IReadOnlyCollection<string> debugIds, CancellationToken cancellationToken = default)
    {
        if (debugIds.Count == 0)
        {
            return ReadOnlyDictionary<string, SourceMapArtifact>.Empty;
        }

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(FindByDebugIdsSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("debug", debugIds.ToArray());
        // Not null on any returned row: the WHERE clause equates debug_id to a member of a non-null
        // array, and SQL equality never holds for NULL.
        return await ReadKeyedAsync(cmd, a => a.DebugId!, cancellationToken);
    }

    /// <summary>The newest artifact for each of <paramref name="filenames"/> in that release, keyed by filename.</summary>
    public async Task<IReadOnlyDictionary<string, SourceMapArtifact>> FindByReleaseFilesAsync(
        long projectId, string release, IReadOnlyCollection<string> filenames,
        CancellationToken cancellationToken = default)
    {
        if (filenames.Count == 0)
        {
            return ReadOnlyDictionary<string, SourceMapArtifact>.Empty;
        }

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(FindByReleaseFilesSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("release", release);
        cmd.Parameters.AddWithValue("filenames", filenames.ToArray());
        return await ReadKeyedAsync(cmd, a => a.Filename, cancellationToken);
    }

    private static async Task<IReadOnlyDictionary<string, SourceMapArtifact>> ReadKeyedAsync(
        NpgsqlCommand cmd, Func<SourceMapArtifact, string> key, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, SourceMapArtifact>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var artifact = Map(reader);
            found[key(artifact)] = artifact;
        }

        return found;
    }

    private static SourceMapArtifact Map(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetInt64(1), r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.GetString(5), r.GetString(6), r.GetInt64(7), r.GetFieldValue<DateTimeOffset>(8));
}
