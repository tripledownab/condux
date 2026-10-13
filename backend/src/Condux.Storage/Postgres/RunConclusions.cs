using Condux.Core.FixEngine;
using Condux.Core.Plans;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// Where a fix run's conclusion and its allowance move together, for both run tables. Each
/// conclusion is one transaction that locks the run's row, writes the outcome, and refunds or takes back
/// the run, so a crash between the two can neither keep a failed run's reservation nor refund a run twice.
/// The stale-run sweep also fails and refunds in one transaction of its own, guarded by its WHERE clause
/// rather than a lock, and waits on the lock taken here.
/// </summary>
internal static class RunConclusions
{
    /// <summary>What a conclusion needs about a run, read under its row lock: the lock is what lets the
    /// status check stand, since the sweep's update waits for it.</summary>
    private sealed record LockedRun(
        FixStatus Status, FixFailureReason? Reason, long OrgId, Tier Tier, DateTimeOffset ReservedAt);

    /// <summary>Fails the run and refunds it, but only while it is still pending or running. The lock
    /// statement must select status, failure_reason, org_id, tier and the reservation instant, in that
    /// order, for the row named by @id, with FOR UPDATE.</summary>
    internal static Task<bool> TryFailAsync(
        string connectionString, string lockSql, Guid runId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<int>> writeFailure, CancellationToken cancellationToken) =>
        TryConcludeInProgressAsync(connectionString, lockSql, runId, writeFailure, cancellationToken);

    /// <summary>Withdraws the run and refunds it, but only when the withdrawal's own guarded write lands: that
    /// write, not this method, says which runs nothing holds, since that turns on lease columns the
    /// lock statement does not read. The lock statement is the one <see cref="TryFailAsync"/> takes.</summary>
    internal static Task<bool> TryWithdrawAsync(
        string connectionString, string lockSql, Guid runId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<int>> writeWithdrawal, CancellationToken cancellationToken) =>
        TryConcludeInProgressAsync(connectionString, lockSql, runId, writeWithdrawal, cancellationToken);

    private static async Task<bool> TryConcludeInProgressAsync(
        string connectionString, string lockSql, Guid runId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<int>> write, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        if (await LockAsync(conn, tx, lockSql, runId, cancellationToken) is not { } run
            || run.Status is not (FixStatus.Pending or FixStatus.Running)
            || await write(conn, tx) != 1)
        {
            return false;
        }
        await PostgresAiFixQuota.RefundAsync(conn, tx, run.OrgId, run.ReservedAt, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Records the run's success, and takes the run back when the sweep had failed and refunded it,
    /// which is the only writer of <see cref="FixFailureReason.Interrupted"/> on a hosted run (a runner may
    /// report it too, but a runner's run is concluded by its report, never here).</summary>
    internal static async Task RecordSuccessAsync(
        string connectionString, string lockSql, Guid runId,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<int>> writeSuccess, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        if (await LockAsync(conn, tx, lockSql, runId, cancellationToken) is not { } run)
        {
            return; // Deleted with its issue or repository: there is nothing left to record.
        }
        await writeSuccess(conn, tx);
        // The limit is the org's plan now, not when the run was reserved, like every other reservation.
        if (run is { Status: FixStatus.Failed, Reason: FixFailureReason.Interrupted })
        {
            await PostgresAiFixQuota.RechargeAsync(
                conn, tx, run.OrgId, run.ReservedAt, PlanCatalog.For(run.Tier).AiFixesPerMonth, cancellationToken);
        }
        await tx.CommitAsync(cancellationToken);
    }

    private static async Task<LockedRun?> LockAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string lockSql, Guid runId, CancellationToken cancellationToken)
    {
        await using var cmd = new NpgsqlCommand(lockSql, conn, tx);
        cmd.Parameters.AddWithValue("id", runId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return new LockedRun(
            (FixStatus)reader.GetInt16(0), FixFailureColumn.Read(reader, 1), reader.GetInt64(2),
            (Tier)reader.GetInt16(3), reader.GetFieldValue<DateTimeOffset>(4));
    }
}
