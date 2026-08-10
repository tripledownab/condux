using Condux.Core.Repos;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>Release → commit associations, in Postgres. A fix runs against the release's commit.</summary>
public sealed class ReleaseRepository(string connectionString)
{
    private const string RecordSql = """
        INSERT INTO releases (id, project_id, repo_link_id, version, commit_sha)
        VALUES (@id, @project, @repo, @version, @sha)
        ON CONFLICT (project_id, version) DO UPDATE
          SET repo_link_id = EXCLUDED.repo_link_id, commit_sha = EXCLUDED.commit_sha
        RETURNING id, project_id, repo_link_id, version, commit_sha, created_at;
        """;

    private const string ListSql = """
        SELECT id, project_id, repo_link_id, version, commit_sha, created_at
        FROM releases WHERE project_id = @project ORDER BY created_at DESC LIMIT @limit;
        """;

    private const string GetByVersionSql = """
        SELECT id, project_id, repo_link_id, version, commit_sha, created_at
        FROM releases WHERE project_id = @project AND version = @version LIMIT 1;
        """;

    public async Task<Release> RecordAsync(
        long projectId, Guid repoLinkId, string version, string commitSha, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(RecordSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("repo", repoLinkId);
        cmd.Parameters.AddWithValue("version", version);
        cmd.Parameters.AddWithValue("sha", commitSha);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return Map(reader);
    }

    public async Task<IReadOnlyList<Release>> ListByProjectAsync(
        long projectId, int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ListSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("limit", limit);
        var list = new List<Release>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Map(reader));
        }
        return list;
    }

    // The release recorded for a project version (the fix engine resolves an issue's first_release → its
    // commit, #144). Null when no release was recorded for that version.
    public async Task<Release?> GetByVersionAsync(
        long projectId, string version, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(GetByVersionSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("version", version);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    private static Release Map(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetInt64(1), r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5));
}
