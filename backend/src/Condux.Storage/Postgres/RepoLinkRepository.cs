using Condux.Core.Repos;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>Repo links (repo ↔ project) and their code mappings, in Postgres.</summary>
public sealed class RepoLinkRepository(string connectionString)
{
    private const string LinkSql = """
        INSERT INTO repo_links (id, project_id, repo_full_name, default_branch)
        VALUES (@id, @project, @repo, @branch)
        ON CONFLICT (project_id, repo_full_name) DO UPDATE SET default_branch = EXCLUDED.default_branch
        RETURNING id, project_id, repo_full_name, default_branch, created_at;
        """;

    private const string ListSql = """
        SELECT id, project_id, repo_full_name, default_branch, created_at
        FROM repo_links WHERE project_id = @project ORDER BY created_at;
        """;

    private const string GetSql = """
        SELECT id, project_id, repo_full_name, default_branch, created_at
        FROM repo_links WHERE project_id = @project AND id = @id;
        """;

    private const string AddMappingSql = """
        INSERT INTO code_mappings (id, repo_link_id, stack_root, source_root)
        VALUES (@id, @repo, @stackRoot, @sourceRoot)
        RETURNING id, repo_link_id, stack_root, source_root;
        """;

    private const string ListMappingsSql = """
        SELECT id, repo_link_id, stack_root, source_root
        FROM code_mappings WHERE repo_link_id = @repo ORDER BY stack_root;
        """;

    public async Task<RepoLink> LinkAsync(
        long projectId, string repoFullName, string defaultBranch, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(LinkSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("repo", repoFullName);
        cmd.Parameters.AddWithValue("branch", defaultBranch);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return MapRepo(reader);
    }

    public async Task<IReadOnlyList<RepoLink>> ListByProjectAsync(
        long projectId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ListSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        var list = new List<RepoLink>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(MapRepo(reader));
        }
        return list;
    }

    /// <summary>Change the branch draft PRs target for a linked repo. False when the link does not
    /// exist in the project (the caller 404s).</summary>
    public async Task<bool> UpdateDefaultBranchAsync(
        long projectId, Guid id, string defaultBranch, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "UPDATE repo_links SET default_branch = @branch WHERE id = @id AND project_id = @project;", conn);
        cmd.Parameters.AddWithValue("branch", defaultBranch);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("project", projectId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Fetch a repo link by id, scoped to its project (so a caller can only touch its own). Null if absent.</summary>
    public async Task<RepoLink?> GetAsync(long projectId, Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(GetSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapRepo(reader) : null;
    }

    /// <summary>The project a repo link belongs to, or null. What the CVE worker needs to nudge the
    /// project's live dashboard after a run — its job carries only the repo link id (ADR-0030).</summary>
    public async Task<long?> GetProjectIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT project_id FROM repo_links WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", id);
        return await cmd.ExecuteScalarAsync(cancellationToken) is long projectId ? projectId : null;
    }

    /// <summary>Unlink a repo from its project. Its code mappings and releases cascade away
    /// (FK ON DELETE CASCADE). False when the link is not in the project (the caller 404s).</summary>
    public async Task<bool> DeleteAsync(long projectId, Guid id, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM repo_links WHERE id = @id AND project_id = @project;", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("project", projectId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Delete a single code mapping, scoped to its repo link (which the caller has already
    /// checked belongs to the project). False when the mapping is not on that repo (the caller 404s).</summary>
    public async Task<bool> DeleteCodeMappingAsync(
        Guid repoLinkId, Guid mappingId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM code_mappings WHERE id = @id AND repo_link_id = @repo;", conn);
        cmd.Parameters.AddWithValue("id", mappingId);
        cmd.Parameters.AddWithValue("repo", repoLinkId);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<CodeMapping> AddCodeMappingAsync(
        Guid repoLinkId, string stackRoot, string sourceRoot, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(AddMappingSql, conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("repo", repoLinkId);
        cmd.Parameters.AddWithValue("stackRoot", stackRoot);
        cmd.Parameters.AddWithValue("sourceRoot", sourceRoot);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return MapMapping(reader);
    }

    public async Task<IReadOnlyList<CodeMapping>> ListCodeMappingsAsync(
        Guid repoLinkId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ListMappingsSql, conn);
        cmd.Parameters.AddWithValue("repo", repoLinkId);
        var list = new List<CodeMapping>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(MapMapping(reader));
        }
        return list;
    }

    private static RepoLink MapRepo(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTimeOffset>(4));

    private static CodeMapping MapMapping(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3));
}
