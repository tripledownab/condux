using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.Core.Issues;
using Npgsql;

namespace Condux.Storage.Postgres;

/// <summary>The result of upserting an issue: its internal id, the new (1-based) occurrence count, its
/// public id, and whether this event <c>Reopened</c> a previously resolved issue (a regression). A
/// brand-new issue has <c>Occurrence == 1</c>; a regression has <c>Reopened</c> true.</summary>
public readonly record struct UpsertResult(long Id, long Occurrence, Guid PublicId, bool Reopened);

/// <summary>A grouped issue as returned to the API. <c>Id</c> is the public, non-enumerable UUID.</summary>
public sealed record IssueSummary(
    Guid Id,
    long ProjectId,
    string Fingerprint,
    string Title,
    string Culprit,
    int Level,
    int Status,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    long EventCount,
    long? AssigneeUserId,
    string? FirstRelease);

/// <summary>An issue resolved by its public id, carrying the internal id the event store keys on.</summary>
public readonly record struct IssueRecord(long InternalId, IssueSummary Summary);

/// <summary>The per-week issue movement for an org's projects (ADR-0031): issues first seen in the window,
/// reopened in the window (regressions), resolved in the window, and the current open count.</summary>
public readonly record struct WeeklyIssueCounts(long New, long Regressed, long Resolved, long Open);

/// <summary>Reads and upserts grouped issues in Postgres, keyed by (project, fingerprint).</summary>
public sealed class IssueRepository(string connectionString)
{
    // New issues get a UUIDv7 (time-ordered) as their public id; on conflict the existing public_id
    // is preserved (it is not in the DO UPDATE set). A new event on a resolved issue (status 2) reopens
    // it — a regression. The `prior` CTE captures the pre-upsert status (NULL when the row is new) so
    // RETURNING can report the reopen without a second round trip.
    private const string UpsertSql = """
        WITH prior AS (
            SELECT status FROM issues WHERE project_id = @project AND fingerprint = @fp
        )
        INSERT INTO issues (project_id, fingerprint, title, culprit, level, first_seen, last_seen, event_count, public_id, first_release, activated_at)
        VALUES (@project, @fp, @title, @culprit, @level, @seen, @seen, 1, @public_id, @release, @seen)
        ON CONFLICT (project_id, fingerprint) DO UPDATE
          SET event_count = issues.event_count + 1,
              last_seen   = GREATEST(issues.last_seen, EXCLUDED.last_seen),
              title       = EXCLUDED.title,
              culprit     = EXCLUDED.culprit,
              level       = EXCLUDED.level,
              status      = CASE WHEN issues.status = 2 THEN 1 ELSE issues.status END,
              -- Re-activate on a regression (a resolved issue reopening) so the new-issues badge counts it
              -- again; an ordinary repeat event leaves activated_at untouched (ADR-0030).
              activated_at = CASE WHEN issues.status = 2 THEN EXCLUDED.last_seen ELSE issues.activated_at END,
              -- A regression clears the resolution stamp (the issue is open again); the weekly digest counts
              -- resolved_at within the week (ADR-0031).
              resolved_at = CASE WHEN issues.status = 2 THEN NULL ELSE issues.resolved_at END,
              -- Keep the first release seen; only fill it once an event actually carries one.
              first_release = COALESCE(issues.first_release, EXCLUDED.first_release)
        RETURNING id, event_count, public_id, (SELECT status FROM prior);
        """;

    private const string GetSql = """
        SELECT id, public_id, project_id, fingerprint, title, culprit, level, status, first_seen, last_seen, event_count, assignee_user_id, first_release
        FROM issues
        WHERE project_id = @project AND public_id = @public_id;
        """;

    private const string UpdateAssigneeSql = """
        UPDATE issues SET assignee_user_id = @assignee
        WHERE project_id = @project AND public_id = @public_id;
        """;

    /// <summary>Assign (or with null, unassign) an issue. False if not found.</summary>
    public async Task<bool> UpdateAssigneeAsync(
        long projectId, Guid publicId, long? assigneeUserId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(UpdateAssigneeSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("public_id", publicId);
        cmd.Parameters.AddWithValue("assignee", (object?)assigneeUserId ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    // Resolving stamps resolved_at (powers the weekly "resolved" count, ADR-0031); reopening clears it;
    // ignoring leaves it untouched.
    private const string UpdateStatusSql = """
        UPDATE issues
          SET status = @status,
              resolved_at = CASE WHEN @status = 2 THEN now() WHEN @status = 1 THEN NULL ELSE resolved_at END
        WHERE project_id = @project AND public_id = @public_id;
        """;

    /// <summary>Set an issue's triage status (1 unresolved, 2 resolved, 3 ignored). False if not found.</summary>
    public async Task<bool> UpdateStatusAsync(
        long projectId, Guid publicId, int status, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(UpdateStatusSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("public_id", publicId);
        cmd.Parameters.AddWithValue("status", (short)status);
        return await cmd.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>Upsert an issue for a grouped event; returns its internal id and new occurrence count.</summary>
    public async Task<UpsertResult> UpsertAsync(
        long projectId, Grouping grouping, Level level, DateTimeOffset seenAt, string? release = null,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(UpsertSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("fp", grouping.Fingerprint);
        cmd.Parameters.AddWithValue("title", grouping.Title);
        cmd.Parameters.AddWithValue("culprit", grouping.Culprit);
        cmd.Parameters.AddWithValue("level", (short)level);
        cmd.Parameters.AddWithValue("seen", seenAt);
        cmd.Parameters.AddWithValue("public_id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("release", (object?)release ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        // Column 3 is the prior status (NULL for a new issue); status 2 was "resolved", so seeing it
        // here means this event reopened the issue.
        var priorStatus = reader.IsDBNull(3) ? (short?)null : reader.GetInt16(3);
        return new UpsertResult(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetGuid(2), priorStatus == 2);
    }

    private const string SummaryColumns =
        "public_id, project_id, fingerprint, title, culprit, level, status, first_seen, last_seen, event_count, assignee_user_id, first_release";

    /// <summary>A filtered, sorted, offset-paginated page of a project's issues, plus whether more remain
    /// (fetches one extra row to decide). The filter + sort are applied server-side (Sentry-style subset).</summary>
    public async Task<(IReadOnlyList<IssueSummary> Issues, bool HasMore)> ListPageAsync(
        long projectId, IssueFilter filter, long currentUserId, string sort, int limit, int offset,
        CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        var sql = $"SELECT {SummaryColumns} FROM issues{IssueFilterSql.Where(filter)} " +
                  $"ORDER BY {IssueFilterSql.OrderColumn(sort)} DESC LIMIT @limit OFFSET @offset;";
        await using var cmd = new NpgsqlCommand(sql, conn);
        IssueFilterSql.AddParams(cmd, projectId, filter, currentUserId);
        cmd.Parameters.AddWithValue("limit", limit + 1);
        cmd.Parameters.AddWithValue("offset", offset);

        var list = new List<IssueSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(MapSummary(reader, 0));
        }
        var hasMore = list.Count > limit;
        return (hasMore ? list.Take(limit).ToList() : list, hasMore);
    }

    /// <summary>Counts per status and per level for the project under the query. Each facet ignores its own
    /// selection so the rail shows every option's total in the current context (Sentry-style facet counts).</summary>
    public async Task<(IReadOnlyDictionary<int, long> ByStatus, IReadOnlyDictionary<int, long> ByLevel)> CountsAsync(
        long projectId, IssueFilter filter, long currentUserId, CancellationToken cancellationToken = default)
    {
        var byStatus = await GroupCountAsync(projectId, filter with { Status = null }, currentUserId, "status", cancellationToken);
        var byLevel = await GroupCountAsync(projectId, filter with { Level = null }, currentUserId, "level", cancellationToken);
        return (byStatus, byLevel);
    }

    private async Task<IReadOnlyDictionary<int, long>> GroupCountAsync(
        long projectId, IssueFilter filter, long currentUserId, string column, CancellationToken cancellationToken)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        var sql = $"SELECT {column}, count(*) FROM issues{IssueFilterSql.Where(filter)} GROUP BY {column};";
        await using var cmd = new NpgsqlCommand(sql, conn);
        IssueFilterSql.AddParams(cmd, projectId, filter, currentUserId);
        var map = new Dictionary<int, long>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            map[reader.GetInt16(0)] = reader.GetInt64(1);
        }
        return map;
    }

    /// <summary>The internal id → public id map for a project's issues — the join the sparkline batch
    /// endpoint needs, since ClickHouse keys on the internal bigint but the API only exposes the UUID.</summary>
    public async Task<IReadOnlyDictionary<long, Guid>> PublicIdsByProjectAsync(
        long projectId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT id, public_id FROM issues WHERE project_id = @project;", conn);
        cmd.Parameters.AddWithValue("project", projectId);

        var map = new Dictionary<long, Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            map[reader.GetInt64(0)] = reader.GetGuid(1);
        }
        return map;
    }

    /// <summary>The week's issue movement across the given projects (an org's), computed in one grouped
    /// query: new (first_seen in window), regressed (reactivated in window but first seen before it),
    /// resolved (resolved_at in window), and the current open (status 1) count. Zeros for an empty project
    /// set (so an org with no projects is a cheap no-op, no <c>ANY</c> over an empty array).</summary>
    public async Task<WeeklyIssueCounts> WeeklyIssueCountsAsync(
        IReadOnlyList<long> projectIds, DateTimeOffset start, DateTimeOffset end,
        CancellationToken cancellationToken = default)
    {
        if (projectIds.Count == 0)
        {
            return default;
        }

        const string sql = """
            SELECT
                count(*) FILTER (WHERE first_seen >= @start AND first_seen < @end) AS new_issues,
                count(*) FILTER (WHERE activated_at >= @start AND activated_at < @end AND first_seen < @start) AS regressed,
                count(*) FILTER (WHERE resolved_at >= @start AND resolved_at < @end) AS resolved,
                count(*) FILTER (WHERE status = 1) AS open_now
            FROM issues
            WHERE project_id = ANY(@projects);
            """;
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("projects", projectIds.ToArray());
        cmd.Parameters.AddWithValue("start", start);
        cmd.Parameters.AddWithValue("end", end);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new WeeklyIssueCounts(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    /// <summary>Issue summaries by internal id (the id ClickHouse keys on), keyed back by that id — the join
    /// the weekly digest needs to turn ranked stat rows into titles. Empty map for an empty id set.</summary>
    public async Task<IReadOnlyDictionary<long, IssueSummary>> SummariesByInternalIdsAsync(
        IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<long, IssueSummary>();
        if (ids.Count == 0)
        {
            return map;
        }

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            $"SELECT id, {SummaryColumns} FROM issues WHERE id = ANY(@ids);", conn);
        cmd.Parameters.AddWithValue("ids", ids.ToArray());
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            map[reader.GetInt64(0)] = MapSummary(reader, 1);
        }
        return map;
    }

    /// <summary>Resolve an issue by its public id, scoped to its project. Null if not found.</summary>
    public async Task<IssueRecord?> GetByPublicIdAsync(
        long projectId, Guid publicId, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(GetSql, conn);
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("public_id", publicId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new IssueRecord(reader.GetInt64(0), MapSummary(reader, 1))
            : null;
    }

    // Reads an IssueSummary starting at column `i` (public_id, project_id, fingerprint, ...).
    private static IssueSummary MapSummary(NpgsqlDataReader reader, int i) => new(
        reader.GetGuid(i),
        reader.GetInt64(i + 1),
        reader.GetString(i + 2),
        reader.GetString(i + 3),
        reader.GetString(i + 4),
        reader.GetInt16(i + 5),
        reader.GetInt16(i + 6),
        reader.GetFieldValue<DateTimeOffset>(i + 7),
        reader.GetFieldValue<DateTimeOffset>(i + 8),
        reader.GetInt64(i + 9),
        reader.IsDBNull(i + 10) ? null : reader.GetInt64(i + 10),
        reader.IsDBNull(i + 11) ? null : reader.GetString(i + 11));
}
