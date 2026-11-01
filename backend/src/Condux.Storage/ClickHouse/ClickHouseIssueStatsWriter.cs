namespace Condux.Storage.ClickHouse;

/// <summary>One counting row for condux.issue_stats_1h (JSON keys match the column names). count is a
/// SimpleAggregateFunction(sum), so per-event rows of 1 merge into the hourly sum in the background.</summary>
public sealed record IssueStatsRow(
    string project_id,
    ulong issue_id,
    long bucket,
    ulong count,
    long first_seen,
    long last_seen);

/// <summary>One counting row for condux.issue_received_1h: an event counted in the hour Condux received it
/// rather than the hour it claims (migration 0006), which the weekly digest reads.</summary>
public sealed record IssueReceivedRow(string project_id, ulong issue_id, long bucket, ulong count);

/// <summary>
/// Writes per-event counting rows to the issue_stats_1h rollup and its receipt-time sibling issue_received_1h,
/// for EVERY event, before the per-issue sampling decision, so both rollups are exact while raw event storage
/// stays sampled (#102). Same batched JSONEachRow HTTP path as the event writer.
/// </summary>
public sealed class ClickHouseIssueStatsWriter(HttpClient http)
{
    public Task InsertAsync(IReadOnlyList<IssueStatsRow> rows, CancellationToken cancellationToken = default) =>
        ClickHouseInsert.RowsAsync(http, "condux.issue_stats_1h", rows, cancellationToken);

    public Task InsertReceivedAsync(IReadOnlyList<IssueReceivedRow> rows, CancellationToken cancellationToken = default) =>
        ClickHouseInsert.RowsAsync(http, "condux.issue_received_1h", rows, cancellationToken);

    /// <summary>A counting row for <paramref name="count"/> events seen at <paramref name="seenAt"/>, in its
    /// UTC hour bucket.</summary>
    public static IssueStatsRow ToRow(string projectId, ulong issueId, DateTimeOffset seenAt, ulong count = 1)
    {
        // Unix numbers, never text (see ClickHouseInsert).
        var seconds = seenAt.ToUnixTimeSeconds();
        var seen = seenAt.ToUnixTimeMilliseconds();
        return new IssueStatsRow(projectId, issueId, seconds - (seconds % 3600), count, seen, seen);
    }

    /// <summary>A counting row for <paramref name="count"/> events received at <paramref name="receivedAt"/>,
    /// in its UTC hour bucket.</summary>
    public static IssueReceivedRow ToReceivedRow(
        string projectId, ulong issueId, DateTimeOffset receivedAt, ulong count = 1)
    {
        var seconds = receivedAt.ToUnixTimeSeconds();
        return new IssueReceivedRow(projectId, issueId, seconds - (seconds % 3600), count);
    }
}
