using Condux.Core.FixEngine;
using Condux.Core.Plans;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>A fix as a row in the project-wide Fixes list (#118): the run joined to its issue for
/// context, plus whether the current user has viewed it.</summary>
public sealed record FixListItem(
    Guid Id,
    Guid IssuePublicId,
    string IssueTitle,
    int IssueLevel,
    FixStatus Status,
    VerifyStatus VerifyStatus,
    string RepoFullName,
    string PrUrl,
    string Summary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool Viewed);

/// <summary>One entry of a fix's append-only audit trail, for the detail timeline.</summary>
public sealed record FixAuditEntry(string Actor, string Event, string Detail, DateTimeOffset CreatedAt);

/// <summary>A fix run in full for the detail pane: every field, its issue context, viewed/archived
/// flags and the audit timeline.</summary>
public sealed record FixDetail(
    Guid Id,
    Guid IssuePublicId,
    string IssueTitle,
    int IssueLevel,
    FixStatus Status,
    VerifyStatus VerifyStatus,
    string RepoFullName,
    string Provider,
    string Model,
    string Branch,
    string PrUrl,
    string Summary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? MergedAt,
    DateTimeOffset? VerifiedAt,
    bool Archived,
    bool Viewed,
    long InputTokens,
    long OutputTokens,
    decimal? CostUsd,
    IReadOnlyList<FixAuditEntry> Audit);

/// <summary>Conductor spend for one model over a window (#120): the run count, token totals and the
/// derived USD cost (null when the model has no published rate, e.g. a bring-your-own model).</summary>
public sealed record FixModelCost(
    string Model, int RunCount, long InputTokens, long OutputTokens, decimal? CostUsd);

/// <summary>A project's Conductor spend over a window: the overall totals plus a per-model breakdown.
/// <c>TotalUsd</c> sums only the models we can price; unpriced models still contribute their tokens.</summary>
public sealed record FixCostRollup(
    decimal TotalUsd, int RunCount, long InputTokens, long OutputTokens, IReadOnlyList<FixModelCost> ByModel);

/// <summary>
/// The read/write model behind the Fixes section (#118): the project-wide list, single-fix detail with
/// its audit trail, per-user viewed tracking (the needs-attention badge) and soft archive. A fix's
/// project is its issue's project, so every query joins <c>issues</c> and scopes by <c>project_id</c>
/// for tenancy. Separate from <see cref="PostgresFixStore"/> so the worker's <see cref="IFixStore"/>
/// contract stays untouched.
/// </summary>
public sealed class PostgresFixCatalog(string connectionString)
{
    // active = true lists non-archived fixes, false lists archived ones.
    private const string ListSql = """
        SELECT f.id, i.public_id, i.title, i.level, f.status, f.verify_status,
               f.repo_full_name, f.pr_url, f.summary, f.created_at, f.updated_at,
               (v.user_id IS NOT NULL) AS viewed
        FROM fix_suggestions f
        JOIN issues i ON i.id = f.issue_id
        LEFT JOIN fix_views v ON v.fix_id = f.id AND v.user_id = @user
        WHERE i.project_id = @project AND (f.archived_at IS NULL) = @active
        ORDER BY f.created_at DESC
        LIMIT 200;
        """;

    private const string DetailSql = """
        SELECT f.id, i.public_id, i.title, i.level, f.status, f.verify_status,
               f.repo_full_name, f.provider, f.model, f.branch, f.pr_url, f.summary,
               f.created_at, f.updated_at, f.merged_at, f.verified_at,
               (f.archived_at IS NOT NULL) AS archived, (v.user_id IS NOT NULL) AS viewed,
               f.input_tokens, f.output_tokens
        FROM fix_suggestions f
        JOIN issues i ON i.id = f.issue_id
        LEFT JOIN fix_views v ON v.fix_id = f.id AND v.user_id = @user
        WHERE i.project_id = @project AND f.id = @fix;
        """;

    // Conductor spend over a window (#120): a per-model rollup of the runs that actually consumed model
    // tokens (so the no-model fake/simulated runs and failed pre-model runs, which are 0, drop out). Cost
    // is derived from ModelPricing in-process, not in SQL, so the rates stay a single source in Core.
    private const string CostRollupSql = """
        SELECT f.model, count(*), coalesce(sum(f.input_tokens), 0), coalesce(sum(f.output_tokens), 0)
        FROM fix_suggestions f
        JOIN issues i ON i.id = f.issue_id
        WHERE i.project_id = @project AND f.created_at >= @since
          AND (f.input_tokens > 0 OR f.output_tokens > 0)
        GROUP BY f.model
        ORDER BY sum(f.input_tokens + f.output_tokens) DESC;
        """;

    private const string AuditSql = """
        SELECT actor, event, detail::text, created_at
        FROM fix_audit WHERE fix_id = @fix ORDER BY created_at;
        """;

    // Tenancy-guarded upsert: only marks a fix that belongs to the project; idempotent.
    private const string MarkViewedSql = """
        INSERT INTO fix_views (fix_id, user_id)
        SELECT f.id, @user FROM fix_suggestions f JOIN issues i ON i.id = f.issue_id
        WHERE f.id = @fix AND i.project_id = @project
        ON CONFLICT (fix_id, user_id) DO NOTHING;
        """;

    private const string SetArchivedSql = """
        UPDATE fix_suggestions SET archived_at = @ts, updated_at = now()
        WHERE id = @fix AND issue_id IN (SELECT id FROM issues WHERE project_id = @project);
        """;

    // The needs-attention rule (FixAttention) lives in Core, so the badge count fetches the candidate
    // (status, verify) pairs and applies it here rather than duplicating the rule in SQL.
    private const string AttentionCandidatesSql = """
        SELECT f.status, f.verify_status
        FROM fix_suggestions f
        JOIN issues i ON i.id = f.issue_id
        LEFT JOIN fix_views v ON v.fix_id = f.id AND v.user_id = @user
        WHERE i.project_id = @project AND f.archived_at IS NULL AND v.user_id IS NULL;
        """;

    public async Task<IReadOnlyList<FixListItem>> ListAsync(
        long projectId, long userId, bool active, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(ListSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("user", userId);
        cmd.Parameters.AddWithValue("active", active);
        var list = new List<FixListItem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new FixListItem(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt16(3),
                (FixStatus)reader.GetInt16(4), (VerifyStatus)reader.GetInt16(5),
                reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.GetFieldValue<DateTimeOffset>(9), reader.GetFieldValue<DateTimeOffset>(10),
                reader.GetBoolean(11)));
        }
        return list;
    }

    public async Task<FixDetail?> GetAsync(
        long projectId, Guid fixId, long userId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);

        FixDetail? detail;
        await using (var cmd = new NpgsqlCommand(DetailSql, conn))
        {
            cmd.Parameters.AddWithValue("project", projectId);
            cmd.Parameters.AddWithValue("fix", fixId);
            cmd.Parameters.AddWithValue("user", userId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }
            var model = reader.GetString(8);
            var inputTokens = reader.GetInt64(18);
            var outputTokens = reader.GetInt64(19);
            detail = new FixDetail(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetInt16(3),
                (FixStatus)reader.GetInt16(4), (VerifyStatus)reader.GetInt16(5),
                reader.GetString(6), reader.GetString(7), model, reader.GetString(9),
                reader.GetString(10), reader.GetString(11),
                reader.GetFieldValue<DateTimeOffset>(12), reader.GetFieldValue<DateTimeOffset>(13),
                reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
                reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
                reader.GetBoolean(16), reader.GetBoolean(17),
                inputTokens, outputTokens, ModelPricing.EstimateUsd(model, inputTokens, outputTokens), []);
        }

        await using var auditCmd = new NpgsqlCommand(AuditSql, conn);
        auditCmd.Parameters.AddWithValue("fix", fixId);
        var audit = new List<FixAuditEntry>();
        await using var auditReader = await auditCmd.ExecuteReaderAsync(cancellationToken);
        while (await auditReader.ReadAsync(cancellationToken))
        {
            audit.Add(new FixAuditEntry(
                auditReader.GetString(0), auditReader.GetString(1), auditReader.GetString(2),
                auditReader.GetFieldValue<DateTimeOffset>(3)));
        }
        return detail with { Audit = audit };
    }

    public async Task MarkViewedAsync(
        long projectId, Guid fixId, long userId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(MarkViewedSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("fix", fixId);
        cmd.Parameters.AddWithValue("user", userId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Archive or restore a fix. False when the fix is not in the project (404).</summary>
    public async Task<bool> SetArchivedAsync(
        long projectId, Guid fixId, bool archived, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(SetArchivedSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("fix", fixId);
        cmd.Parameters.AddWithValue("ts", archived ? DateTimeOffset.UtcNow : DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Count of non-archived, unviewed fixes that need the user's attention — the nav badge.</summary>
    public async Task<int> CountNeedsAttentionAsync(
        long projectId, long userId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(AttentionCandidatesSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("user", userId);
        var count = 0;
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (FixAttention.NeedsAttention((FixStatus)reader.GetInt16(0), (VerifyStatus)reader.GetInt16(1)))
            {
                count++;
            }
        }
        return count;
    }

    /// <summary>Conductor spend for a project since <paramref name="sinceUtc"/>: per-model token totals
    /// priced via <see cref="ModelPricing"/>, plus the overall totals. The total sums only priced models
    /// (a BYO model contributes tokens but no cost).</summary>
    public async Task<FixCostRollup> CostRollupAsync(
        long projectId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(CostRollupSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("since", sinceUtc);

        var byModel = new List<FixModelCost>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var model = reader.GetString(0);
            var runs = (int)reader.GetInt64(1);
            var input = reader.GetInt64(2);
            var output = reader.GetInt64(3);
            byModel.Add(new FixModelCost(model, runs, input, output, ModelPricing.EstimateUsd(model, input, output)));
        }

        return new FixCostRollup(
            byModel.Sum(m => m.CostUsd ?? 0m),
            byModel.Sum(m => m.RunCount),
            byModel.Sum(m => m.InputTokens),
            byModel.Sum(m => m.OutputTokens),
            byModel);
    }
}
