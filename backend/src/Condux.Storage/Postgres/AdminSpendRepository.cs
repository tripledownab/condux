using Condux.Core.Plans;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>Conductor spend for one model within one org (or aggregated), priced via <see cref="ModelPricing"/>.</summary>
public sealed record AdminSpendModelRow(
    string Model, int RunCount, long InputTokens, long OutputTokens, decimal? CostUsd);

/// <summary>Conductor spend rolled up to one org across all its models.</summary>
public sealed record AdminSpendOrgRow(
    long OrgId, string OrgName, int RunCount, long InputTokens, long OutputTokens, decimal? CostUsd);

/// <summary>A cross-org (or single-org) spend rollup: grand total plus per-model and per-org breakdowns.</summary>
public sealed record AdminSpendRollup(
    decimal TotalUsd, IReadOnlyList<AdminSpendModelRow> ByModel, IReadOnlyList<AdminSpendOrgRow> ByOrg);

/// <summary>One Conductor run for the per-run drill-down (issue fix or CVE bump).</summary>
public sealed record AdminSpendRunRow(
    Guid Id, long OrgId, string OrgName, string Model, string Kind, int Status,
    long InputTokens, long OutputTokens, decimal? CostUsd, DateTimeOffset CreatedAt);

/// <summary>
/// Cross-tenant Conductor spend for the super-admin console (ADR-0027). Reuses the same
/// <c>fix_suggestions ⋃ cve_fix_runs</c> union as <see cref="PostgresAiFixSpend"/> (issue fixes joined
/// issue → project → org, CVE bumps joined repo → project → org), priced in-process via
/// <see cref="ModelPricing"/> — the single pricing source, which returns null for a bring-your-own /
/// unpriced model (tokens still shown, dollars unknown). Deliberately cross-tenant, so it only ever runs
/// behind the platform-admin gate. Lists are bounded by <see cref="MaxRows"/> (no pagination yet).
/// </summary>
public sealed class AdminSpendRepository(string connectionString)
{
    /// <summary>Row cap for the per-run drill-down until real pagination lands.</summary>
    public const int MaxRows = 500;

    // Per (org, model) token sums. Only runs that consumed tokens count toward spend (matches the
    // FixCostRollup / PostgresAiFixSpend semantics); an @org of NULL means every org.
    private const string RollupSql = """
        SELECT org_id, org_name, model,
               count(*), coalesce(sum(input_tokens), 0), coalesce(sum(output_tokens), 0)
        FROM (
            SELECT o.id AS org_id, o.name AS org_name, f.model, f.input_tokens, f.output_tokens
            FROM fix_suggestions f
            JOIN issues i ON i.id = f.issue_id
            JOIN projects p ON p.id = i.project_id
            JOIN orgs o ON o.id = p.org_id
            WHERE f.created_at >= @since AND (@org::bigint IS NULL OR o.id = @org::bigint)
              AND (f.input_tokens > 0 OR f.output_tokens > 0)
            UNION ALL
            SELECT o.id, o.name, c.model, c.input_tokens, c.output_tokens
            FROM cve_fix_runs c
            JOIN repo_links r ON r.id = c.repo_link_id
            JOIN projects p ON p.id = r.project_id
            JOIN orgs o ON o.id = p.org_id
            WHERE c.created_at >= @since AND (@org::bigint IS NULL OR o.id = @org::bigint)
              AND (c.input_tokens > 0 OR c.output_tokens > 0)
        ) runs
        GROUP BY org_id, org_name, model;
        """;

    // The per-run drill-down keeps ALL runs (no token filter) so a failed / 0-token run is still listed
    // with cost null. @org and @kind are optional filters.
    private const string RunsSql = """
        SELECT id, org_id, org_name, model, status, input_tokens, output_tokens, created_at, kind
        FROM (
            SELECT f.id, o.id AS org_id, o.name AS org_name, f.model, f.status,
                   f.input_tokens, f.output_tokens, f.created_at, 'issue_fix' AS kind
            FROM fix_suggestions f
            JOIN issues i ON i.id = f.issue_id
            JOIN projects p ON p.id = i.project_id
            JOIN orgs o ON o.id = p.org_id
            WHERE f.created_at >= @since AND (@org::bigint IS NULL OR o.id = @org::bigint)
            UNION ALL
            SELECT c.id, o.id, o.name, c.model, c.status, c.input_tokens, c.output_tokens,
                   c.created_at, 'cve_bump' AS kind
            FROM cve_fix_runs c
            JOIN repo_links r ON r.id = c.repo_link_id
            JOIN projects p ON p.id = r.project_id
            JOIN orgs o ON o.id = p.org_id
            WHERE c.created_at >= @since AND (@org::bigint IS NULL OR o.id = @org::bigint)
        ) runs
        WHERE (@kind::text IS NULL OR kind = @kind::text)
        ORDER BY created_at DESC
        LIMIT @limit;
        """;

    /// <summary>Spend since <paramref name="since"/>, optionally for one org. Prices each (org, model)
    /// bucket via <see cref="ModelPricing"/> and aggregates to per-model, per-org, and grand total.</summary>
    public async Task<AdminSpendRollup> RollupAsync(
        DateTimeOffset since, long? orgId = null, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(RollupSql, conn);
        cmd.Parameters.AddWithValue("since", since);
        cmd.Parameters.AddWithValue("org", (object?)orgId ?? DBNull.Value);

        // Accumulate each (org, model) bucket into per-model and per-org tallies; pricing is per model, so
        // an org's cost is the sum of its buckets' priced costs (an org can span several models/rates).
        var byModel = new Dictionary<string, Tally>();
        var byOrg = new Dictionary<long, OrgTally>();
        var total = 0m;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var orgIdValue = reader.GetInt64(0);
            var orgName = reader.GetString(1);
            var model = reader.GetString(2);
            var runs = (int)reader.GetInt64(3);
            var input = reader.GetInt64(4);
            var output = reader.GetInt64(5);
            var cost = ModelPricing.EstimateUsd(model, input, output);
            total += cost ?? 0m;

            var m = byModel.GetValueOrDefault(model);
            byModel[model] = m.Add(runs, input, output, cost);

            var o = byOrg.GetValueOrDefault(orgIdValue, new OrgTally(orgName, default));
            byOrg[orgIdValue] = o with { Tally = o.Tally.Add(runs, input, output, cost) };
        }

        var models = byModel
            .Select(kv => new AdminSpendModelRow(
                kv.Key, kv.Value.Runs, kv.Value.Input, kv.Value.Output, kv.Value.Cost))
            .OrderByDescending(r => r.CostUsd ?? 0m).ThenBy(r => r.Model).ToList();
        var orgs = byOrg
            .Select(kv => new AdminSpendOrgRow(
                kv.Key, kv.Value.Name, kv.Value.Tally.Runs, kv.Value.Tally.Input, kv.Value.Tally.Output,
                kv.Value.Tally.Cost))
            .OrderByDescending(r => r.CostUsd ?? 0m).ThenBy(r => r.OrgName).ToList();
        return new AdminSpendRollup(total, models, orgs);
    }

    /// <summary>The most recent Conductor runs across all orgs (or one), newest first. Includes failed and
    /// 0-token runs (cost null). Bounded by <see cref="MaxRows"/>.</summary>
    public async Task<IReadOnlyList<AdminSpendRunRow>> ListRunsAsync(
        DateTimeOffset since, long? orgId = null, string? kind = null, int limit = 200,
        CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(RunsSql, conn);
        cmd.Parameters.AddWithValue("since", since);
        cmd.Parameters.AddWithValue("org", (object?)orgId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("kind", (object?)kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, MaxRows));

        var list = new List<AdminSpendRunRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var model = reader.GetString(3);
            var input = reader.GetInt64(5);
            var output = reader.GetInt64(6);
            list.Add(new AdminSpendRunRow(
                reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), model, reader.GetString(8),
                reader.GetInt16(4), input, output, ModelPricing.EstimateUsd(model, input, output),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }
        return list;
    }

    // Running per-model / per-org token + cost accumulator (cost stays null until a priced run lands).
    private readonly record struct Tally(int Runs, long Input, long Output, decimal? Cost)
    {
        public Tally Add(int runs, long input, long output, decimal? cost) => new(
            Runs + runs, Input + input, Output + output,
            cost is null ? Cost : (Cost ?? 0m) + cost.Value);
    }

    private readonly record struct OrgTally(string Name, Tally Tally);
}
