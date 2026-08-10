using Condux.Core.FixEngine;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A fix under post-merge watch, joined to its issue so the watcher can query ClickHouse
/// (internal issue id, project id as the ClickHouse string key) and resolve via the public id.</summary>
public sealed record WatchedFix(
    Guid FixId, long IssueId, Guid IssuePublicId, long ProjectId, DateTimeOffset MergedAt);

/// <summary>The verification-loop queries over <c>fix_suggestions</c> (ADR-0019): mark a fix merged
/// when its draft PR merges, list fixes under watch and record the outcome. Separate from
/// <see cref="PostgresFixStore"/> so <see cref="IFixStore"/> and its test fakes stay untouched.</summary>
public sealed class PostgresFixVerification(string connectionString)
{
    // A run is eligible once it succeeded (a draft PR exists) and has not been marked merged yet.
    // Matching on repo + branch ties the webhook's PR to the run that opened it.
    private const string MarkMergedSql = """
        UPDATE fix_suggestions
          SET merged_at = @merged, verify_status = @watching, updated_at = @merged
        WHERE repo_full_name = @repo AND branch = @branch AND status = @succeeded AND merged_at IS NULL
        RETURNING id;
        """;

    private const string ListWatchingSql = """
        SELECT f.id, f.issue_id, i.public_id, i.project_id, f.merged_at
        FROM fix_suggestions f
        JOIN issues i ON i.id = f.issue_id
        WHERE f.verify_status = @watching;
        """;

    private const string SetStatusSql = """
        UPDATE fix_suggestions
          SET verify_status = @status, verified_at = @verified, updated_at = @verified
        WHERE id = @id AND verify_status = @watching;
        """;

    /// <summary>Marks every succeeded, unmerged run for the repo + branch as merged and watching.
    /// Returns the ids of the runs that flipped (normally one).</summary>
    public async Task<IReadOnlyList<Guid>> MarkMergedAsync(
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
        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }
        return ids;
    }

    public async Task<IReadOnlyList<WatchedFix>> ListWatchingAsync(CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ListWatchingSql, conn);
        cmd.Parameters.AddWithValue("watching", (short)VerifyStatus.Watching);
        var list = new List<WatchedFix>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new WatchedFix(
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetGuid(2),
                reader.GetInt64(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }
        return list;
    }

    /// <summary>Records the verification outcome for a watching fix. Returns false when the fix was
    /// no longer watching (already concluded by a concurrent tick).</summary>
    public async Task<bool> SetVerifyStatusAsync(
        Guid fixId, VerifyStatus status, DateTimeOffset verifiedAt, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(SetStatusSql, conn);
        cmd.Parameters.AddWithValue("id", fixId);
        cmd.Parameters.AddWithValue("status", (short)status);
        cmd.Parameters.AddWithValue("verified", verifiedAt);
        cmd.Parameters.AddWithValue("watching", (short)VerifyStatus.Watching);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
