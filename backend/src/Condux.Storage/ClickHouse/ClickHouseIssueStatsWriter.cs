using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Condux.Storage.ClickHouse;

/// <summary>One counting row for condux.issue_stats_1h (JSON keys match the column names). count is a
/// SimpleAggregateFunction(sum), so per-event rows of 1 merge into the hourly sum in the background.</summary>
public sealed record IssueStatsRow(
    string project_id,
    ulong issue_id,
    string bucket,
    ulong count,
    string first_seen,
    string last_seen);

/// <summary>
/// Writes per-event counting rows to the issue_stats_1h rollup — for EVERY event, before the per-issue
/// sampling decision, so the rollup is exact while raw event storage stays sampled (#102). Same batched
/// JSONEachRow HTTP path as the event writer.
/// </summary>
public sealed class ClickHouseIssueStatsWriter(HttpClient http)
{
    public async Task InsertAsync(IReadOnlyList<IssueStatsRow> rows, CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var body = new StringBuilder();
        foreach (var row in rows)
        {
            body.Append(JsonSerializer.Serialize(row)).Append('\n');
        }

        var url = $"/?query={Uri.EscapeDataString("INSERT INTO condux.issue_stats_1h FORMAT JSONEachRow")}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8),
        };

        using var resp = await http.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>One counting row for an event seen at <paramref name="seenAt"/> (UTC hour bucket).</summary>
    public static IssueStatsRow ToRow(string projectId, ulong issueId, DateTimeOffset seenAt)
    {
        var utc = seenAt.UtcDateTime;
        var bucket = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var seen = utc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        return new IssueStatsRow(projectId, issueId, bucket, 1, seen, seen);
    }
}
