using Condux.Core.CveFix;
using Condux.Core.FixEngine;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>The reads of <see cref="PostgresCveFixStore"/>: a repository's bumps, and whether an advisory
/// has one in progress.</summary>
public sealed partial class PostgresCveFixStore
{
    private const string Columns =
        "id, repo_link_id, ghsa_id, cve_id, package, ecosystem, from_range, to_version, advisory_url, "
        + "status, provider, model, branch, pr_url, summary, actor, created_at, updated_at, "
        + "input_tokens, output_tokens, failure_reason, merged_at, verify_status, verified_at, verify_evidence::text";

    private const string ListByRepoSql = $"""
        SELECT {Columns}
        FROM cve_fix_runs
        WHERE repo_link_id = @repo
        ORDER BY created_at DESC;
        """;

    // 1=pending, 2=running: the in-flight states the active guard checks.
    private const string HasActiveSql = """
        SELECT 1 FROM cve_fix_runs
        WHERE repo_link_id = @repo AND ghsa_id = @ghsa AND status IN (1, 2)
        LIMIT 1;
        """;

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
        FailureReason = FixFailureColumn.Read(r, 20),
        MergedAt = r.IsDBNull(21) ? null : r.GetFieldValue<DateTimeOffset>(21),
        VerifyStatus = (VerifyStatus)r.GetInt16(22),
        VerifiedAt = r.IsDBNull(23) ? null : r.GetFieldValue<DateTimeOffset>(23),
        VerifyEvidence = r.IsDBNull(24) ? null : CveFixEvidence.Parse(r.GetString(24)),
    };
}
