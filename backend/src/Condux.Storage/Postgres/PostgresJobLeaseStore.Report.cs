using Condux.Core.FixEngine;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// How a runner ends a leased run: the outcome it reports, proved by its lease id, for either run kind.
/// The CVE bump's statement lives with the rest of that kind in the Cve partial.
/// </summary>
public sealed partial class PostgresJobLeaseStore
{
    // Same guard on the way out. A late report from a superseded runner would otherwise overwrite the
    // outcome the runner that actually finished the work wrote.
    //
    // The model is only known once a runner has run the job, since it resolves its own, so it is written
    // here rather than at enqueue. COALESCE keeps whatever is there when a runner reports none. Returns
    // the project id (via the issue, which the FK guarantees) so the caller can nudge that project's
    // live badges without a second query, and the org and month a failed run's refund goes to.
    private const string ReportSql = """
        UPDATE fix_suggestions SET
            status = @status, branch = @branch, pr_url = @pr, summary = @summary,
            model = COALESCE(NULLIF(@model, ''), model),
            input_tokens = @inputTokens, output_tokens = @outputTokens, failure_reason = @reason,
            lease_expires_at = NULL, updated_at = now()
        FROM issues i JOIN projects p ON p.id = i.project_id
        WHERE fix_suggestions.id = @fix AND i.id = fix_suggestions.issue_id
          AND fix_suggestions.leased_by = @runner AND fix_suggestions.lease_expires_at > @now
        RETURNING i.project_id, p.org_id, COALESCE(fix_suggestions.reserved_at, fix_suggestions.created_at);
        """;

    /// <summary>Record the outcome, proved by the lease id, and refund a run that did not succeed in the same
    /// transaction, so a crash between the two can neither keep its reservation nor refund it twice. Returns
    /// the run's project id and kind (for the caller's live-badge nudge and kind-specific audit), or null when
    /// the lease was lost and the report is refused. Tried against both run tables, same guard as the
    /// heartbeat.</summary>
    public async Task<ReportedJob?> TryReportAsync(
        Guid fixId, string leaseId, FixStatus status, string branch, string prUrl, string summary,
        string model, long inputTokens, long outputTokens, DateTimeOffset now,
        CancellationToken cancellationToken = default, FixFailureReason? failureReason = null)
    {
        if (await ConcludeAsync(ReportSql, fixId, leaseId, status, branch, prUrl, summary,
                model, inputTokens, outputTokens, now, failureReason, cancellationToken) is { } fix)
        {
            return new ReportedJob(fix, JobKind.IssueFix);
        }

        return await ConcludeAsync(CveReportSql, fixId, leaseId, status, branch, prUrl, summary,
                model, inputTokens, outputTokens, now, failureReason, cancellationToken) is { } bump
            ? new ReportedJob(bump, JobKind.CveFix)
            : null;
    }

    private async Task<long?> ConcludeAsync(
        string sql, Guid fixId, string leaseId, FixStatus status, string branch, string prUrl,
        string summary, string model, long inputTokens, long outputTokens, DateTimeOffset now,
        FixFailureReason? failureReason, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("fix", fixId);
        cmd.Parameters.AddWithValue("runner", leaseId);
        cmd.Parameters.AddWithValue("status", (short)status);
        cmd.Parameters.AddWithValue("branch", branch);
        cmd.Parameters.AddWithValue("model", model);
        cmd.Parameters.AddWithValue("pr", prUrl);
        cmd.Parameters.AddWithValue("summary", summary);
        cmd.Parameters.AddWithValue("inputTokens", inputTokens);
        cmd.Parameters.AddWithValue("outputTokens", outputTokens);
        cmd.Parameters.AddWithValue("now", now);
        cmd.Parameters.AddWithValue("reason", FixFailureColumn.ToParameter(failureReason));
        long projectId, orgId;
        DateTimeOffset reservedAt;
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            (projectId, orgId, reservedAt) =
                (reader.GetInt64(0), reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(2));
        }
        // Only a terminal status reaches here (the endpoint narrows it), and every one but a success refunds.
        if (status != FixStatus.Succeeded)
        {
            await PostgresAiFixQuota.RefundAsync(conn, tx, orgId, reservedAt, cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        return projectId;
    }
}
