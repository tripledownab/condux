using Condux.Core.FixEngine;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A merged CVE bump under watch, with what its verdict needs: the project whose inventory to
/// read, the package and target, and every vulnerable range stored at request time (empty for a run
/// requested before they were).</summary>
public sealed record WatchedCveFix(
    Guid RunId,
    long ProjectId,
    string Package,
    string Ecosystem,
    string ToVersion,
    IReadOnlyList<string> VulnerableRanges,
    DateTimeOffset MergedAt);

/// <summary>The verification queries over <c>cve_fix_runs</c> (ADR-0051), the CVE twin of
/// <see cref="PostgresFixVerification"/>: mark a bump merged when its draft PR merges, list bumps under
/// watch, and record a conclusion with its evidence.</summary>
public sealed class PostgresCveFixVerification(string connectionString)
{
    // A run is eligible once it succeeded (a draft PR exists) and has not been marked merged yet. The run
    // keeps no repository name of its own, so the webhook's repository is matched through its link.
    private const string MarkMergedSql = """
        UPDATE cve_fix_runs r
          SET merged_at = @merged, verify_status = @watching, updated_at = @merged
        FROM repo_links l
        WHERE l.id = r.repo_link_id AND l.repo_full_name = @repo AND r.branch = @branch
          AND r.status = @succeeded AND r.merged_at IS NULL
        RETURNING r.id, l.project_id;
        """;

    private const string ListWatchingSql = """
        SELECT r.id, l.project_id, r.package, r.ecosystem, r.to_version, r.vulnerable_ranges, r.merged_at
        FROM cve_fix_runs r
        JOIN repo_links l ON l.id = r.repo_link_id
        WHERE r.verify_status = @watching;
        """;

    private const string SetStatusSql = """
        UPDATE cve_fix_runs
          SET verify_status = @status, verified_at = @verified, verify_evidence = @evidence::jsonb,
              updated_at = @verified
        WHERE id = @id AND verify_status = @watching;
        """;

    /// <summary>Marks every succeeded, unmerged bump for the repository and branch as merged and watching.
    /// Returns each run that flipped with its project, so the caller can nudge it (normally one).</summary>
    public async Task<IReadOnlyList<(Guid RunId, long ProjectId)>> MarkMergedAsync(
        string repoFullName, string branch, DateTimeOffset mergedAt, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(MarkMergedSql, conn);
        cmd.Parameters.AddWithValue("repo", repoFullName);
        cmd.Parameters.AddWithValue("branch", branch);
        cmd.Parameters.AddWithValue("merged", mergedAt);
        cmd.Parameters.AddWithValue("watching", (short)VerifyStatus.Watching);
        cmd.Parameters.AddWithValue("succeeded", (short)FixStatus.Succeeded);
        var runs = new List<(Guid, long)>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add((reader.GetGuid(0), reader.GetInt64(1)));
        }
        return runs;
    }

    public async Task<IReadOnlyList<WatchedCveFix>> ListWatchingAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ListWatchingSql, conn);
        cmd.Parameters.AddWithValue("watching", (short)VerifyStatus.Watching);
        var list = new List<WatchedCveFix>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new WatchedCveFix(
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? [] : reader.GetFieldValue<string[]>(5),
                reader.GetFieldValue<DateTimeOffset>(6)));
        }
        return list;
    }

    /// <summary>Records a watching bump's conclusion and the evidence it rested on. False when the run was
    /// no longer watching (already concluded by a concurrent tick).</summary>
    public async Task<bool> SetVerifyStatusAsync(
        Guid runId, VerifyStatus status, string evidenceJson, DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(SetStatusSql, conn);
        cmd.Parameters.AddWithValue("id", runId);
        cmd.Parameters.AddWithValue("status", (short)status);
        cmd.Parameters.AddWithValue("evidence", evidenceJson);
        cmd.Parameters.AddWithValue("verified", verifiedAt);
        cmd.Parameters.AddWithValue("watching", (short)VerifyStatus.Watching);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
