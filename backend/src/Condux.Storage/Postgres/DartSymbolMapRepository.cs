using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Condux.Storage.Postgres;

/// <summary>
/// Flutter obfuscation maps in Postgres, one row per name under the debug file of the build it belongs to
/// (<c>dart_symbol_names</c>), so a read looks up the few names an event carries through the primary key,
/// however large the map. The debug file row carries what the map costs for the quota, and its deletion takes
/// the names with it.
/// </summary>
public sealed class DartSymbolMapRepository(string connectionString)
{
    // The newest build of the project with that debug id, the one a lookup reads. Builds share a debug id
    // when their build IDs agree in their first 16 bytes, which an uploader can arrange as often as it likes,
    // so the map goes to one build rather than to each. Locked, so an eviction deleting it waits for this
    // upload rather than racing its names.
    private const string BuildSql = """
        SELECT id FROM debug_files WHERE project_id = @project AND debug_id = @debug
        ORDER BY created_at DESC LIMIT 1 FOR UPDATE;
        """;

    private const string ClearSql = "DELETE FROM dart_symbol_names WHERE debug_file_id = @id;";

    // A map upload is an upload of the build's artifacts, so like a debug file upload again it makes the
    // build the newest: counted as recent by the quota, and the last to be evicted.
    private const string MarkSql =
        "UPDATE debug_files SET dart_symbol_map_bytes = @bytes, created_at = now() WHERE id = @id;";

    /// <summary>What the quota charges for one name: a fixed cost per row and index entry, plus each name's
    /// UTF-8 bytes, the form Postgres stores them in, the obfuscated name four times since it is held in the
    /// row and again in the index, which insertion order makes larger still. Measured in Postgres 17: 715,000
    /// short names held 116 bytes a name beyond their JSON, 256 character names 745 bytes a name in ASCII, and
    /// 256 random three-byte characters 2,401 bytes a name, written in shuffled order; this charges more than
    /// each, so a map costs no less than Postgres holds for it.</summary>
    public static long ToCost(string obfuscated, string original) =>
        120 + 4L * Encoding.UTF8.GetByteCount(obfuscated) + 2L * Encoding.UTF8.GetByteCount(original);

    // Writing a large map takes seconds, longer under load, and Npgsql's default of 30 seconds would cut it
    // off part way and leave Postgres finishing the work for nobody.
    private const int WriteTimeoutSeconds = 120;

    // The newest build among the event's images that has a map, as DebugFileRepository picks the newest of a
    // debug id, then only the names asked for.
    private const string LookUpSql = """
        WITH build AS (
            SELECT id FROM debug_files
            WHERE project_id = @project AND debug_id = ANY(@debug) AND dart_symbol_map_bytes > 0
            ORDER BY created_at DESC LIMIT 1)
        SELECT n.obfuscated, n.original
        FROM build JOIN dart_symbol_names n ON n.debug_file_id = build.id
        WHERE n.obfuscated = ANY(@names);
        """;

    /// <summary>Holds a map for the newest of the project's builds with this debug id, replacing any it held,
    /// and says whether there was one: none means no debug file of that build was uploaded.</summary>
    public async Task<bool> RecordAsync(
        long projectId, string debugId, IReadOnlyDictionary<string, string> map,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(connectionString) { CommandTimeout = WriteTimeoutSeconds }.ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        Guid id;
        await using (var build = new NpgsqlCommand(BuildSql, conn, tx))
        {
            build.Parameters.AddWithValue("project", projectId);
            build.Parameters.AddWithValue("debug", debugId);
            if (await build.ExecuteScalarAsync(cancellationToken) is not Guid found)
            {
                return false;
            }
            id = found;
        }
        await using (var clear = new NpgsqlCommand(ClearSql, conn, tx))
        {
            clear.Parameters.AddWithValue("id", id);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var copy = await conn.BeginBinaryImportAsync(
            "COPY dart_symbol_names (debug_file_id, obfuscated, original) FROM STDIN (FORMAT BINARY)",
            cancellationToken))
        {
            foreach (var (obfuscated, original) in map)
            {
                await copy.StartRowAsync(cancellationToken);
                await copy.WriteAsync(id, NpgsqlDbType.Uuid, cancellationToken);
                await copy.WriteAsync(obfuscated, NpgsqlDbType.Text, cancellationToken);
                await copy.WriteAsync(original, NpgsqlDbType.Text, cancellationToken);
            }
            await copy.CompleteAsync(cancellationToken);
        }
        await using (var mark = new NpgsqlCommand(MarkSql, conn, tx))
        {
            mark.Parameters.AddWithValue("bytes", map.Sum(pair => ToCost(pair.Key, pair.Value)));
            mark.Parameters.AddWithValue("id", id);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>The original names of those <paramref name="names"/> the map of the newest of these builds
    /// holds, keyed by obfuscated name; empty when no build among them has a map.</summary>
    public async Task<IReadOnlyDictionary<string, string>> LookUpAsync(
        long projectId, IReadOnlyCollection<string> debugIds, IReadOnlyCollection<string> names,
        CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        if (debugIds.Count == 0 || names.Count == 0)
        {
            return found;
        }
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(LookUpSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("debug", debugIds.ToArray());
        cmd.Parameters.AddWithValue("names", names.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            found[reader.GetString(0)] = reader.GetString(1);
        }
        return found;
    }
}
