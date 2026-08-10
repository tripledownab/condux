using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A project under an org. The numeric <see cref="Id"/> is the internal + ingest identity (the
/// DSN, the /api/{id}/store/ URL, the issues FK); <see cref="PublicId"/> is the opaque UUIDv7 exposed in
/// dashboard URLs and the project resource API, so <c>/projects/&lt;uuid&gt;</c> is non-enumerable.</summary>
public sealed record ProjectRecord(
    long Id, long OrgId, string Name, string Platform, DateTimeOffset CreatedAt, Guid PublicId);

/// <summary>Creates, lists, and reads projects.</summary>
public sealed class ProjectRepository(string connectionString)
{
    private const string Columns = "id, org_id, name, platform, created_at, public_id";

    private const string InsertSql = """
        INSERT INTO projects (org_id, name, platform, public_id)
        VALUES (@org, @name, @platform, @public_id)
        RETURNING id, org_id, name, platform, created_at, public_id;
        """;

    private const string ListSql =
        $"SELECT {Columns} FROM projects WHERE org_id = @org ORDER BY id;";

    private const string GetSql = $"SELECT {Columns} FROM projects WHERE id = @id;";

    private const string GetByPublicIdSql = $"SELECT {Columns} FROM projects WHERE public_id = @public_id;";

    private const string UpdateSql = """
        UPDATE projects SET name = @name, platform = @platform
        WHERE id = @id
        RETURNING id, org_id, name, platform, created_at, public_id;
        """;

    // Deleting a project cascades to its DSN keys, issues, repo links, releases, and alert rules (all
    // FKs are ON DELETE CASCADE). ClickHouse events are separate and expire via their retention TTL.
    private const string DeleteSql = "DELETE FROM projects WHERE id = @id;";

    public async Task<ProjectRecord> CreateAsync(
        long orgId, string name, string platform, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("platform", platform);
        // New projects get a time-ordered UUIDv7 (keeps the unique index healthy), like issues (#91).
        cmd.Parameters.AddWithValue("public_id", Guid.CreateVersion7());
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return Read(reader);
    }

    public async Task<IReadOnlyList<ProjectRecord>> ListByOrgAsync(long orgId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(ListSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);

        var list = new List<ProjectRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public async Task<ProjectRecord?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(GetSql, conn);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>Read a project by its public UUID — the id exposed in dashboard URLs and the resource API.</summary>
    public async Task<ProjectRecord?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(GetByPublicIdSql, conn);
        cmd.Parameters.AddWithValue("public_id", publicId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>Rename a project (name + platform). Null if it no longer exists.</summary>
    public async Task<ProjectRecord?> UpdateAsync(
        long id, string name, string platform, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(UpdateSql, conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("name", name);
        cmd.Parameters.AddWithValue("platform", platform);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    /// <summary>Delete a project and everything scoped to it (cascade). False if it was already gone.</summary>
    public async Task<bool> DeleteAsync(long id, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(DeleteSql, conn);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static ProjectRecord Read(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
        r.GetFieldValue<DateTimeOffset>(4), r.GetGuid(5));
}
