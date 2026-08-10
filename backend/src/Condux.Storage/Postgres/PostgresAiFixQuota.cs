using Condux.Core.Quotas;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// Postgres-backed <see cref="IAiFixQuota"/> over the <c>ai_fix_quota</c> row (one per org). Consumption
/// is a single atomic upsert: first use inserts the row, a new calendar month resets the counter, and the
/// limit guard sits in the WHERE clause — so under the row lock a reservation either lands or reports the
/// allowance spent, with no read-then-write race.
/// </summary>
public sealed class PostgresAiFixQuota(string connectionString) : IAiFixQuota
{
    // No row updated/inserted (no RETURNING) means the guard rejected: same period and used >= limit.
    // limit 0 is uncapped: the guard passes and usage is still tracked.
    private const string ConsumeSql = """
        INSERT INTO ai_fix_quota (org_id, period, used, updated_at)
        VALUES (@org, @period, 1, now())
        ON CONFLICT (org_id) DO UPDATE SET
            used = CASE WHEN ai_fix_quota.period = EXCLUDED.period THEN ai_fix_quota.used + 1 ELSE 1 END,
            period = EXCLUDED.period,
            updated_at = now()
        WHERE ai_fix_quota.period <> EXCLUDED.period
           OR @limit = 0
           OR ai_fix_quota.used < @limit
        RETURNING used;
        """;

    private const string RefundSql = """
        UPDATE ai_fix_quota SET used = GREATEST(used - 1, 0), updated_at = now()
        WHERE org_id = @org AND period = @period;
        """;

    private const string UsedSql = "SELECT used FROM ai_fix_quota WHERE org_id = @org AND period = @period;";

    // The lifetime grant (#112) lives in its own column that never resets, so it is independent of the
    // monthly counter and unaffected by tier switches. Same reserve-in-the-WHERE-guard shape as the
    // monthly consume; the period column is only set to keep the row valid on first insert.
    private const string ConsumeLifetimeSql = """
        INSERT INTO ai_fix_quota (org_id, period, used, used_lifetime, updated_at)
        VALUES (@org, @period, 0, 1, now())
        ON CONFLICT (org_id) DO UPDATE SET
            used_lifetime = ai_fix_quota.used_lifetime + 1,
            updated_at = now()
        WHERE @limit = 0 OR ai_fix_quota.used_lifetime < @limit
        RETURNING used_lifetime;
        """;

    private const string RefundLifetimeSql =
        "UPDATE ai_fix_quota SET used_lifetime = GREATEST(used_lifetime - 1, 0), updated_at = now() WHERE org_id = @org;";

    private const string LifetimeUsedSql = "SELECT used_lifetime FROM ai_fix_quota WHERE org_id = @org;";

    public async Task<bool> TryConsumeAsync(
        long orgId, int monthlyLimit, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ConsumeSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("period", PeriodOf(nowUtc));
        cmd.Parameters.AddWithValue("limit", monthlyLimit);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task RefundAsync(long orgId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(RefundSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("period", PeriodOf(nowUtc));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> GetUsedAsync(long orgId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(UsedSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("period", PeriodOf(nowUtc));
        return await cmd.ExecuteScalarAsync(cancellationToken) is int used ? used : 0;
    }

    public async Task<bool> TryConsumeLifetimeAsync(
        long orgId, int lifetimeLimit, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ConsumeLifetimeSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("period", PeriodOf(nowUtc));
        cmd.Parameters.AddWithValue("limit", lifetimeLimit);
        return await cmd.ExecuteScalarAsync(cancellationToken) is not null;
    }

    public async Task RefundLifetimeAsync(long orgId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(RefundLifetimeSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> GetLifetimeUsedAsync(long orgId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(LifetimeUsedSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        return await cmd.ExecuteScalarAsync(cancellationToken) is int used ? used : 0;
    }

    // The calendar month the counter covers, as its first day (UTC). DateOnly maps to the DATE column.
    private static DateOnly PeriodOf(DateTimeOffset nowUtc) =>
        new(nowUtc.UtcDateTime.Year, nowUtc.UtcDateTime.Month, 1);
}
