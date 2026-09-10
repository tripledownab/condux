using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// The weekly-summary send ledger (ADR-0031): one row per (org, weekly send) that has been handled. The
/// claim is an atomic <c>INSERT ... ON CONFLICT DO NOTHING RETURNING</c> — it returns a row only for the
/// caller that inserts it, so exactly one worker tick (across restarts and replicas) composes and sends a
/// given week. Uses the pause-notify throttle's guarded-write idiom.
/// </summary>
public sealed class PostgresWeeklySummaryLedger(string connectionString)
{
    private const string ClaimSql = """
        INSERT INTO weekly_summary_sends (org_id, week_start, sent_at)
        VALUES (@org, @week, @now)
        ON CONFLICT (org_id, week_start) DO NOTHING
        RETURNING org_id;
        """;

    /// <summary>Claim this week's send for the org. True if this caller won the claim (should send); false if
    /// the week was already handled.</summary>
    public async Task<bool> TryClaimAsync(
        long orgId, DateOnly weekStart, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ClaimSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("week", weekStart);
        cmd.Parameters.AddWithValue("now", now);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>Release a claim (delete the row) so a week can be re-attempted after a transient send failure.
    /// Idempotent — a no-op if the row is already gone.</summary>
    public async Task ReleaseAsync(long orgId, DateOnly weekStart, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM weekly_summary_sends WHERE org_id = @org AND week_start = @week;", conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("week", weekStart);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
