using Condux.Core.CveFix;
using Condux.Core.FixEngine;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>Postgres-backed <see cref="ICveFixStore"/>: CVE-bump runs in <c>cve_fix_runs</c>.</summary>
public sealed class PostgresCveFixStore(string connectionString) : ICveFixStore
{
    private const string Columns =
        "id, repo_link_id, ghsa_id, cve_id, package, ecosystem, from_range, to_version, advisory_url, "
        + "status, provider, model, branch, pr_url, summary, actor, created_at, updated_at, "
        + "input_tokens, output_tokens";

    private const string InsertSql = """
        INSERT INTO cve_fix_runs
            (id, repo_link_id, ghsa_id, cve_id, package, ecosystem, from_range, to_version, advisory_url,
             status, provider, model, branch, pr_url, summary, actor, created_at, updated_at)
        VALUES (@id, @repo, @ghsa, @cve, @package, @ecosystem, @from, @to, @advisory,
                @status, @provider, @model, @branch, @pr, @summary, @actor, @created, @updated);
        """;

    private const string UpdateSql = """
        UPDATE cve_fix_runs
          SET status = @status, branch = @branch, pr_url = @pr, summary = @summary,
              input_tokens = @in, output_tokens = @out, updated_at = @updated
        WHERE id = @id;
        """;

    private const string ListByRepoSql = $"""
        SELECT {Columns}
        FROM cve_fix_runs
        WHERE repo_link_id = @repo
        ORDER BY created_at DESC;
        """;

    // 1=pending, 2=running — the in-flight states the active guard checks.
    private const string HasActiveSql = """
        SELECT 1 FROM cve_fix_runs
        WHERE repo_link_id = @repo AND ghsa_id = @ghsa AND status IN (1, 2)
        LIMIT 1;
        """;

    public async Task InsertAsync(CveFixRun r, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(InsertSql, conn);
        cmd.Parameters.AddWithValue("id", r.Id);
        cmd.Parameters.AddWithValue("repo", r.RepoLinkId);
        cmd.Parameters.AddWithValue("ghsa", r.GhsaId);
        cmd.Parameters.AddWithValue("cve", (object?)r.CveId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("package", r.Package);
        cmd.Parameters.AddWithValue("ecosystem", r.Ecosystem);
        cmd.Parameters.AddWithValue("from", r.FromRange);
        cmd.Parameters.AddWithValue("to", r.ToVersion);
        cmd.Parameters.AddWithValue("advisory", r.AdvisoryUrl);
        cmd.Parameters.AddWithValue("status", (short)r.Status);
        cmd.Parameters.AddWithValue("provider", r.Provider);
        cmd.Parameters.AddWithValue("model", r.Model);
        cmd.Parameters.AddWithValue("branch", r.Branch);
        cmd.Parameters.AddWithValue("pr", r.PrUrl);
        cmd.Parameters.AddWithValue("summary", r.Summary);
        cmd.Parameters.AddWithValue("actor", r.Actor);
        cmd.Parameters.AddWithValue("created", r.CreatedAt);
        cmd.Parameters.AddWithValue("updated", r.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateAsync(CveFixRun r, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(UpdateSql, conn);
        cmd.Parameters.AddWithValue("id", r.Id);
        cmd.Parameters.AddWithValue("status", (short)r.Status);
        cmd.Parameters.AddWithValue("branch", r.Branch);
        cmd.Parameters.AddWithValue("pr", r.PrUrl);
        cmd.Parameters.AddWithValue("summary", r.Summary);
        cmd.Parameters.AddWithValue("in", r.InputTokens);
        cmd.Parameters.AddWithValue("out", r.OutputTokens);
        cmd.Parameters.AddWithValue("updated", r.UpdatedAt);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CveFixRun>> ListByRepoAsync(
        Guid repoLinkId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ListByRepoSql, conn);
        cmd.Parameters.AddWithValue("repo", repoLinkId);
        var list = new List<CveFixRun>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(Map(reader));
        }
        return list;
    }

    public async Task<bool> HasActiveRunAsync(
        Guid repoLinkId, string ghsaId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(HasActiveSql, conn);
        cmd.Parameters.AddWithValue("repo", repoLinkId);
        cmd.Parameters.AddWithValue("ghsa", ghsaId);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static CveFixRun Map(NpgsqlDataReader r) => new(
        r.GetGuid(0),
        r.GetGuid(1),
        r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.GetString(4),
        r.GetString(5),
        r.GetString(6),
        r.GetString(7),
        r.GetString(8),
        (FixStatus)r.GetInt16(9),
        r.GetString(10),
        r.GetString(11),
        r.GetString(12),
        r.GetString(13),
        r.GetString(14),
        r.GetString(15),
        r.GetFieldValue<DateTimeOffset>(16),
        r.GetFieldValue<DateTimeOffset>(17))
    {
        InputTokens = r.GetInt64(18),
        OutputTokens = r.GetInt64(19),
    };
}
