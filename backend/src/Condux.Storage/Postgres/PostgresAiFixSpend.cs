using Condux.Core.Plans;
using Condux.Core.Quotas;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>
/// Postgres-backed <see cref="IAiFixSpend"/> (#120 budgets): the org's Conductor spend so far this
/// calendar month, summed from the per-run token usage on both run tables — issue fixes
/// (<c>fix_suggestions</c>, joined issue → project → org) and supply-chain CVE bumps
/// (<c>cve_fix_runs</c>, joined repo → project → org, #117) — and priced in-process via
/// <see cref="ModelPricing"/>, the same single source the cost rollup uses. Both fix kinds draw on the
/// same budget, so both count. Only runs that consumed tokens count; an unpriced (BYO) model contributes
/// tokens but no dollars.
///
/// <para>This is spend on <b>our</b> compute, which is what the cost cap governs, so a run executed on the
/// org's own runner (it carries a <c>job_context</c>, ADR-0033 slice 4c — either kind) is excluded. Counting it would
/// meter the customer's own model bill against our fair-use ceiling: an org that spent a month's worth on
/// its own key would then be refused hosted runs, and the cross-org spend rollup would report their money
/// as our cost.</para>
/// </summary>
public sealed class PostgresAiFixSpend(string connectionString) : IAiFixSpend
{
    private const string SpendSql = """
        SELECT model, coalesce(sum(input_tokens), 0), coalesce(sum(output_tokens), 0)
        FROM (
            SELECT f.model, f.input_tokens, f.output_tokens
            FROM fix_suggestions f
            JOIN issues i ON i.id = f.issue_id
            JOIN projects p ON p.id = i.project_id
            WHERE p.org_id = @org AND f.created_at >= @since
              AND f.job_context IS NULL
              AND (f.input_tokens > 0 OR f.output_tokens > 0)
            UNION ALL
            SELECT c.model, c.input_tokens, c.output_tokens
            FROM cve_fix_runs c
            JOIN repo_links r ON r.id = c.repo_link_id
            JOIN projects p ON p.id = r.project_id
            WHERE p.org_id = @org AND c.created_at >= @since
              AND c.job_context IS NULL
              AND (c.input_tokens > 0 OR c.output_tokens > 0)
        ) runs
        GROUP BY model;
        """;

    public async Task<decimal> MonthToDateUsdAsync(
        long orgId, DateTimeOffset nowUtc, CancellationToken cancellationToken = default)
    {
        var monthStart = new DateTimeOffset(nowUtc.UtcDateTime.Year, nowUtc.UtcDateTime.Month, 1, 0, 0, 0, TimeSpan.Zero);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(SpendSql, conn);
        cmd.Parameters.AddWithValue("org", orgId);
        cmd.Parameters.AddWithValue("since", monthStart);

        var total = 0m;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            total += ModelPricing.EstimateUsd(reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)) ?? 0m;
        }
        return total;
    }
}
