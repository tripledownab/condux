using System.Globalization;
using System.Text.Json;

namespace Condux.Storage.ClickHouse;

/// <summary>An issue's event volume this week vs the prior week, for the weekly digest (ADR-0031).</summary>
public readonly record struct IssueVolume(long IssueId, long ThisWeek, long PreviousWeek);

/// <summary>
/// Reads the exact per-issue event counts back from the issue_stats_1h rollup (#102): sparse
/// bucket-start (unix seconds) → count pairs over a window, hourly or re-aggregated to daily. The
/// caller zero-fills via Core's IssueHistogram. The <see cref="HttpClient"/> is pre-configured
/// (base URL + auth + resilience) by <see cref="ClickHouseRegistration"/>.
/// </summary>
public sealed class ClickHouseIssueStatsReader(HttpClient http)
{
    // Bound params keep the query injection-safe; the bucket function must be literal SQL, so the two
    // granularities are two constant queries rather than interpolated.
    private const string HourlySql = """
        SELECT toUnixTimestamp(bucket) AS ts, sum(count) AS c
        FROM condux.issue_stats_1h
        WHERE project_id = {pid:String} AND issue_id = {iid:UInt64}
          AND bucket >= toStartOfHour(now() - INTERVAL {hours:UInt32} HOUR)
        GROUP BY bucket ORDER BY bucket
        FORMAT JSON
        """;

    private const string DailySql = """
        SELECT toUnixTimestamp(toStartOfDay(bucket)) AS ts, sum(count) AS c
        FROM condux.issue_stats_1h
        WHERE project_id = {pid:String} AND issue_id = {iid:UInt64}
          AND bucket >= toStartOfDay(now() - INTERVAL {hours:UInt32} HOUR)
        GROUP BY toStartOfDay(bucket) ORDER BY ts
        FORMAT JSON
        """;

    private const string BatchHourlySql = """
        SELECT issue_id, toUnixTimestamp(bucket) AS ts, sum(count) AS c
        FROM condux.issue_stats_1h
        WHERE project_id = {pid:String}
          AND bucket >= toStartOfHour(now() - INTERVAL {hours:UInt32} HOUR)
        GROUP BY issue_id, bucket ORDER BY issue_id, bucket
        FORMAT JSON
        """;

    // The rollup is hourly, so "since" rounds up to the next full bucket: the bucket containing the
    // merge also holds pre-merge events and must not fail a fix. The verification window (72h) is
    // long enough that skipping the partial first hour cannot flip the outcome.
    private const string CountSinceSql = """
        SELECT sum(count) AS c
        FROM condux.issue_stats_1h
        WHERE project_id = {pid:String} AND issue_id = {iid:UInt64}
          AND bucket >= toStartOfHour(toDateTime({since:Int64})) + INTERVAL 1 HOUR
        FORMAT JSON
        """;

    /// <summary>Total events for the issue in full hourly buckets after <paramref name="since"/> —
    /// the fix-verification occurrence check (ADR-0019).</summary>
    public async Task<long> CountSinceAsync(
        string projectId, ulong issueId, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var url = $"/?param_pid={Uri.EscapeDataString(projectId)}&param_iid={issueId}" +
                  $"&param_since={since.ToUnixTimeSeconds()}&query={Uri.EscapeDataString(CountSinceSql)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        using var resp = await http.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<Response>(stream, cancellationToken: cancellationToken);

        var row = payload?.data is [var only, ..] ? only : null;
        return row is not null && TryReadInt64(row.c, out var count) ? count : 0;
    }

    /// <summary>Sparse hourly bucket counts for every issue of a project over the window — one query
    /// for the whole issue list's sparklines, keyed by the internal issue id.</summary>
    public async Task<IReadOnlyDictionary<long, Dictionary<long, long>>> BucketsByIssueAsync(
        string projectId, int windowHours, CancellationToken cancellationToken = default)
    {
        var url = $"/?param_pid={Uri.EscapeDataString(projectId)}&param_hours={windowHours}" +
                  $"&query={Uri.EscapeDataString(BatchHourlySql)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        using var resp = await http.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<BatchResponse>(
            stream, cancellationToken: cancellationToken);

        var byIssue = new Dictionary<long, Dictionary<long, long>>();
        foreach (var row in payload?.data ?? [])
        {
            if (TryReadInt64(row.issue_id, out var issueId) && TryReadInt64(row.ts, out var ts)
                && TryReadInt64(row.c, out var count))
            {
                (byIssue.TryGetValue(issueId, out var sparse)
                    ? sparse
                    : byIssue[issueId] = []).Add(ts, count);
            }
        }
        return byIssue;
    }

    // Per-issue event volume for a project split into this week vs the prior week, in ONE grouped query over
    // [start, end) — the weekly digest (ADR-0031). sumIf keys off the week cutoff, so the result is one row
    // per issue (not per issue-hour), which scales; the caller sums for the project totals and ranks the top.
    private const string WeeklyVolumeSql = """
        SELECT issue_id,
               sumIf(count, bucket >= toDateTime({cutoff:Int64})) AS this_week,
               sumIf(count, bucket < toDateTime({cutoff:Int64})) AS prev_week
        FROM condux.issue_stats_1h
        WHERE project_id = {pid:String}
          AND bucket >= toDateTime({start:Int64}) AND bucket < toDateTime({end:Int64})
        GROUP BY issue_id
        FORMAT JSON
        """;

    /// <summary>Per-issue this-week vs prior-week volume for the project over [previousStart, weekEnd), split
    /// at <paramref name="weekStart"/> (ADR-0031). One row per issue with events in the window.</summary>
    public async Task<IReadOnlyList<IssueVolume>> WeeklyVolumesAsync(
        string projectId, DateTimeOffset previousStart, DateTimeOffset weekStart, DateTimeOffset weekEnd,
        CancellationToken cancellationToken = default)
    {
        var url = $"/?param_pid={Uri.EscapeDataString(projectId)}" +
                  $"&param_start={previousStart.ToUnixTimeSeconds()}&param_cutoff={weekStart.ToUnixTimeSeconds()}" +
                  $"&param_end={weekEnd.ToUnixTimeSeconds()}&query={Uri.EscapeDataString(WeeklyVolumeSql)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        using var resp = await http.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<VolumeResponse>(stream, cancellationToken: cancellationToken);

        var list = new List<IssueVolume>();
        foreach (var row in payload?.data ?? [])
        {
            if (TryReadInt64(row.issue_id, out var issueId) && TryReadInt64(row.this_week, out var thisWeek)
                && TryReadInt64(row.prev_week, out var previousWeek))
            {
                list.Add(new IssueVolume(issueId, thisWeek, previousWeek));
            }
        }
        return list;
    }

    /// <summary>Sparse bucket counts for the issue over the last <paramref name="windowHours"/> hours,
    /// hourly buckets when <paramref name="daily"/> is false, daily otherwise.</summary>
    public async Task<IReadOnlyDictionary<long, long>> BucketsAsync(
        string projectId, ulong issueId, int windowHours, bool daily, CancellationToken cancellationToken = default)
    {
        var sql = daily ? DailySql : HourlySql;
        var url = $"/?param_pid={Uri.EscapeDataString(projectId)}&param_iid={issueId}" +
                  $"&param_hours={windowHours}&query={Uri.EscapeDataString(sql)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        using var resp = await http.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<Response>(stream, cancellationToken: cancellationToken);

        // FORMAT JSON quotes 64-bit integers (sum -> UInt64 string) but not 32-bit ones
        // (toUnixTimestamp -> UInt32 number), so read both kinds.
        var sparse = new Dictionary<long, long>();
        foreach (var row in payload?.data ?? [])
        {
            if (TryReadInt64(row.ts, out var ts) && TryReadInt64(row.c, out var count))
            {
                sparse[ts] = count;
            }
        }

        return sparse;
    }

    private static bool TryReadInt64(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(
                element.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private sealed record Response(List<Row> data);

    private sealed record Row(JsonElement ts, JsonElement c);

    private sealed record BatchResponse(List<BatchRow> data);

    private sealed record BatchRow(JsonElement issue_id, JsonElement ts, JsonElement c);

    private sealed record VolumeResponse(List<VolumeRow> data);

    private sealed record VolumeRow(JsonElement issue_id, JsonElement this_week, JsonElement prev_week);
}
