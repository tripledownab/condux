using Condux.Core.FixEngine;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A hosted fix run the sweep failed and refunded, with what the caller needs to announce it.</summary>
public sealed record SweptRun(Guid Id, bool IsCveBump, long ProjectId);

/// <summary>
/// Fails the hosted fix runs, of both kinds, that are still pending or running long after they should
/// have finished, and refunds their allowance. The Conductor takes each job off the queue once, so a run
/// it was executing when it stopped is normally not picked up again, and without this its row stays in
/// progress with its allowance reserved for ever.
///
/// The failures and the refunds commit in one transaction. A failed row never matches the sweep again,
/// so a refund written after the commit could be lost for good by a shutdown or an error in between.
/// Each UPDATE's guard is its WHERE clause, so two conductors sweeping at once fail and refund a given run
/// at most once. A runner's rows (those carrying a <c>job_context</c>) are left alone: a lapsed lease
/// already makes them claimable again.
/// </summary>
public sealed partial class PostgresStaleRunSweep(string connectionString)
{
    // Pending (1) and running (2) are written as literals, unlike the rest, so that any plan can match the
    // partial indexes on in-flight runs (migration 0056): a generic plan cannot see a parameter's value.
    // A pending run has its own, longer cutoff: its request wrote it before publishing the job, so it may
    // be waiting behind other runs in the queue, which says nothing about whether anything stopped.
    // A running run is failed only once the source host has been asked for its pull request and had none
    // (@cleared), or when it is as old as the pending cutoff, by which time a host that could not be asked
    // has had a day of passes to answer.
    private const string FailFixesSql = """
        UPDATE fix_suggestions fs
          SET status = @failed, failure_reason = @reason, updated_at = @now
        FROM issues i JOIN projects p ON p.id = i.project_id
        WHERE i.id = fs.issue_id
          AND fs.job_context IS NULL
          AND ((fs.status = 2 AND fs.updated_at < @cutoff AND (fs.id = ANY(@cleared) OR fs.updated_at < @pendingCutoff))
            OR (fs.status = 1 AND fs.updated_at < @pendingCutoff))
        RETURNING fs.id, p.org_id, p.id, COALESCE(fs.reserved_at, fs.created_at);
        """;

    private const string FailCveBumpsSql = """
        UPDATE cve_fix_runs r
          SET status = @failed, failure_reason = @reason, updated_at = @now
        FROM repo_links l JOIN projects p ON p.id = l.project_id
        WHERE l.id = r.repo_link_id
          AND r.job_context IS NULL
          AND ((r.status = 2 AND r.updated_at < @cutoff AND (r.id = ANY(@cleared) OR r.updated_at < @pendingCutoff))
            OR (r.status = 1 AND r.updated_at < @pendingCutoff))
        RETURNING r.id, p.org_id, p.id, COALESCE(r.reserved_at, r.created_at);
        """;

    /// <summary>Fails and refunds every hosted run pending since before <paramref name="pendingCutoff"/>,
    /// and every one running since before <paramref name="cutoff"/> that is in <paramref name="cleared"/> or
    /// is also past <paramref name="pendingCutoff"/>, and returns them. The instants come from the caller,
    /// so the decision and the stamp read one clock.
    /// Each run is refunded to the month its allowance was reserved in: its <c>reserved_at</c>, or its
    /// <c>created_at</c> on a row written before that column existed.</summary>
    public async Task<IReadOnlyList<SweptRun>> FailStaleAsync(
        DateTimeOffset cutoff, DateTimeOffset pendingCutoff, DateTimeOffset now, IReadOnlyCollection<Guid> cleared,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        var failed = new List<(SweptRun Run, long OrgId, DateTimeOffset ReservedAt)>();
        var window = (cutoff, pendingCutoff, now, cleared.ToArray());
        await FailAsync(conn, tx, FailFixesSql, isCveBump: false, window, failed, cancellationToken);
        await FailAsync(conn, tx, FailCveBumpsSql, isCveBump: true, window, failed, cancellationToken);
        // In org order, so two sweeps refunding the same orgs take the counter rows in the same order
        // and cannot each hold the row the other needs.
        foreach (var (_, orgId, reservedAt) in failed.OrderBy(f => f.OrgId))
        {
            await PostgresAiFixQuota.RefundAsync(conn, tx, orgId, reservedAt, cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
        return [.. failed.Select(f => f.Run)];
    }

    private static async Task FailAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string sql, bool isCveBump,
        (DateTimeOffset Cutoff, DateTimeOffset PendingCutoff, DateTimeOffset Now, Guid[] Cleared) window,
        List<(SweptRun, long, DateTimeOffset)> failed, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("failed", (short)FixStatus.Failed);
        cmd.Parameters.AddWithValue("reason", FixFailureColumn.ToParameter(FixFailureReason.Interrupted));
        cmd.Parameters.AddWithValue("cutoff", window.Cutoff);
        cmd.Parameters.AddWithValue("pendingCutoff", window.PendingCutoff);
        cmd.Parameters.AddWithValue("now", window.Now);
        cmd.Parameters.AddWithValue("cleared", window.Cleared);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            failed.Add((
                new SweptRun(reader.GetGuid(0), isCveBump, reader.GetInt64(2)),
                reader.GetInt64(1), reader.GetFieldValue<DateTimeOffset>(3)));
        }
    }
}
