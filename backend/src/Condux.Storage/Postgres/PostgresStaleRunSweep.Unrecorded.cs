using Condux.Core.FixEngine;
using Condux.Core.SourceControl;
using Npgsql;
using NpgsqlTypes;

namespace Condux.Storage.Postgres;

/// <summary>A hosted run left running past the cutoff, with what the source host needs to say whether it
/// opened a pull request before it stopped. <see cref="InstallationId"/> is null when the org has no
/// GitHub installation now; <see cref="Reference"/> is the issue id, or the advisory id for a bump.</summary>
public sealed record UnrecordedRun(
    Guid Id, bool IsCveBump, long ProjectId, string RepoFullName, string Reference, long? InstallationId,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The half of the sweep that asks before it fails. A run can open its draft pull request and then stop
/// before it records it: the process dies between the two, or the success write itself fails. Failing
/// such a run would refund an allowance that was spent and leave a pull request no run records, so the
/// merge webhook would never match it and a retry would open a second one, which GitHub cannot delete.
/// </summary>
public sealed partial class PostgresStaleRunSweep
{
    // The installation is the org's first, as the request paths choose it when they queue a run.
    private const string ListSql = """
        SELECT fs.id, FALSE, p.id, fs.repo_full_name, fs.issue_id::text, fs.updated_at,
               (SELECT gi.installation_id FROM github_installations gi
                 WHERE gi.org_id = p.org_id ORDER BY gi.created_at LIMIT 1)
        FROM fix_suggestions fs
          JOIN issues i ON i.id = fs.issue_id
          JOIN projects p ON p.id = i.project_id
        WHERE fs.job_context IS NULL AND fs.status = 2 AND fs.updated_at < @cutoff
        UNION ALL
        SELECT r.id, TRUE, p.id, l.repo_full_name, r.ghsa_id, r.updated_at,
               (SELECT gi.installation_id FROM github_installations gi
                 WHERE gi.org_id = p.org_id ORDER BY gi.created_at LIMIT 1)
        FROM cve_fix_runs r
          JOIN repo_links l ON l.id = r.repo_link_id
          JOIN projects p ON p.id = l.project_id
        WHERE r.job_context IS NULL AND r.status = 2 AND r.updated_at < @cutoff;
        """;

    // Guarded on the run still running, so a run that concluded meanwhile, by its own write or by another
    // conductor's sweep, keeps what it concluded. No refund to undo: the run was never failed.
    // A pull request already merged is marked merged and watched by the same statement, as the merge
    // webhook would have done (PostgresFixVerification.MarkMergedAsync for an issue fix,
    // PostgresCveFixVerification.MarkMergedAsync for a bump): that webhook fired while the run read as
    // running and matched nothing, and a run written as succeeded is never listed again, so a second write
    // that failed would leave it out of the verification watch for good.
    private const string RecordFixSql = """
        UPDATE fix_suggestions SET status = @succeeded, branch = @branch, pr_url = @pr, updated_at = @now,
          merged_at = @merged,
          verify_status = CASE WHEN @merged IS NULL THEN verify_status ELSE @watching END
        WHERE id = @id AND status = 2 AND job_context IS NULL;
        """;

    private const string RecordCveBumpSql = """
        UPDATE cve_fix_runs SET status = @succeeded, branch = @branch, pr_url = @pr, updated_at = @now,
          merged_at = @merged,
          verify_status = CASE WHEN @merged IS NULL THEN verify_status ELSE @watching END
        WHERE id = @id AND status = 2 AND job_context IS NULL;
        """;

    /// <summary>The hosted runs running since before <paramref name="cutoff"/>, of both kinds.</summary>
    public async Task<IReadOnlyList<UnrecordedRun>> ListRunningAsync(
        DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ListSql, conn);
        cmd.Parameters.AddWithValue("cutoff", cutoff);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var runs = new List<UnrecordedRun>();
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add(new UnrecordedRun(
                reader.GetGuid(0), reader.GetBoolean(1), reader.GetInt64(2), reader.GetString(3),
                reader.GetString(4), reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetFieldValue<DateTimeOffset>(5)));
        }
        return runs;
    }

    /// <summary>Records the pull request a stopped run opened, as its success, and its merge when it was
    /// merged, which starts its verification (ADR-0019 for an issue fix, ADR-0051 for a bump). False when
    /// the run was no longer running.</summary>
    public async Task<bool> TryRecordFoundAsync(
        UnrecordedRun run, string branch, FoundPullRequest found, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(run.IsCveBump ? RecordCveBumpSql : RecordFixSql, conn);
        cmd.Parameters.AddWithValue("succeeded", (short)FixStatus.Succeeded);
        cmd.Parameters.AddWithValue("branch", branch);
        cmd.Parameters.AddWithValue("pr", found.Url);
        cmd.Parameters.Add(new NpgsqlParameter("merged", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)found.MergedAt ?? DBNull.Value,
        });
        cmd.Parameters.AddWithValue("watching", (short)VerifyStatus.Watching);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("id", run.Id);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }
}
