using System.Globalization;
using System.Text.Json;

namespace Condux.Storage.ClickHouse;

/// <summary>A stored raw event, as returned for the issue-detail view.</summary>
public sealed record StoredEvent(
    string EventId,
    string Timestamp,
    string Level,
    string Message,
    string ExceptionType,
    string ExceptionValue,
    string Payload);

/// <summary>
/// Reads raw events back from ClickHouse for the issue-detail view, over the HTTP interface the
/// writer uses. Newest-first, capped by <c>limit</c>. The <see cref="HttpClient"/> is pre-configured
/// (base URL + auth headers + resilience) by <see cref="ClickHouseRegistration"/>.
/// </summary>
public sealed class ClickHouseEventReader(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // Bound params ({name:Type}) keep the query injection-safe over the HTTP interface.
    private const string RecentSql = """
        SELECT event_id, timestamp, level, message, exception_type, exception_value, payload
        FROM condux.events
        WHERE project_id = {pid:String} AND issue_id = {iid:UInt64}
        ORDER BY timestamp DESC
        LIMIT {lim:UInt32}
        FORMAT JSON
        """;

    // A page of an issue's events (newest-first), offset-paginated so the detail loads older events on
    // demand rather than all at once.
    private const string PageSql = """
        SELECT event_id, timestamp, level, message, exception_type, exception_value, payload
        FROM condux.events
        WHERE project_id = {pid:String} AND issue_id = {iid:UInt64}
        ORDER BY timestamp DESC
        LIMIT {lim:UInt32} OFFSET {off:UInt32}
        FORMAT JSON
        """;

    // Recent events across a whole project (any issue), for deriving code mappings from a representative
    // sample of stack frames (#113).
    private const string RecentByProjectSql = """
        SELECT event_id, timestamp, level, message, exception_type, exception_value, payload
        FROM condux.events
        WHERE project_id = {pid:String}
        ORDER BY timestamp DESC
        LIMIT {lim:UInt32}
        FORMAT JSON
        """;

    public async Task<IReadOnlyList<StoredEvent>> RecentByIssueAsync(
        string projectId, ulong issueId, int limit = 50, CancellationToken cancellationToken = default)
    {
        var url = $"/?param_pid={Uri.EscapeDataString(projectId)}" +
                  $"&param_iid={issueId}&param_lim={limit}" +
                  $"&query={Uri.EscapeDataString(RecentSql)}";
        return await QueryAsync(url, cancellationToken);
    }

    /// <summary>A newest-first page of the issue's events (offset/limit), for the lazy-loaded table.</summary>
    public async Task<IReadOnlyList<StoredEvent>> PageByIssueAsync(
        string projectId, ulong issueId, int limit, int offset, CancellationToken cancellationToken = default)
    {
        var url = $"/?param_pid={Uri.EscapeDataString(projectId)}" +
                  $"&param_iid={issueId}&param_lim={limit}&param_off={offset}" +
                  $"&query={Uri.EscapeDataString(PageSql)}";
        return await QueryAsync(url, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredEvent>> RecentByProjectAsync(
        string projectId, int limit = 25, CancellationToken cancellationToken = default)
    {
        var url = $"/?param_pid={Uri.EscapeDataString(projectId)}" +
                  $"&param_lim={limit}" +
                  $"&query={Uri.EscapeDataString(RecentByProjectSql)}";
        return await QueryAsync(url, cancellationToken);
    }

    // Distinct pseudonymous users (user_key, #105) affected in a project over a window — the weekly digest's
    // "users affected" (ADR-0031). Approximate: events are sampled and expire, so this counts distinct users
    // among the stored sample, not every user. Empty user_key rows (no identifier) are excluded.
    private const string UsersAffectedSql = """
        SELECT uniqExact(user_key) AS c
        FROM condux.events
        WHERE project_id = {pid:String} AND user_key != ''
          AND timestamp >= toDateTime({start:Int64}) AND timestamp < toDateTime({end:Int64})
        FORMAT JSON
        """;

    /// <summary>Approximate distinct users affected in the project over [start, end) (ADR-0031). Approximate
    /// because <c>events</c> is sampled and short-retention; 0 when nothing matches.</summary>
    public async Task<long> CountUsersAffectedAsync(
        string projectId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken = default)
    {
        var url = $"/?param_pid={Uri.EscapeDataString(projectId)}" +
                  $"&param_start={start.ToUnixTimeSeconds()}&param_end={end.ToUnixTimeSeconds()}" +
                  $"&query={Uri.EscapeDataString(UsersAffectedSql)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<CountResponse>(stream, JsonOptions, cancellationToken);
        // FORMAT JSON renders a 64-bit uniqExact as a quoted string, but read a bare number too (as the sibling
        // stats reader does), so a changed 64-bit quoting setting cannot break the parse.
        return payload?.Data is [{ C: var c }, ..] ? ReadLong(c) : 0;
    }

    private static long ReadLong(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.TryGetInt64(out var number) ? number : 0,
        JsonValueKind.String => long.TryParse(
            element.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
        _ => 0,
    };

    private async Task<IReadOnlyList<StoredEvent>> QueryAsync(string url, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await http.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<Response>(stream, JsonOptions, cancellationToken);
        return payload?.Data ?? [];
    }

    // ClickHouse FORMAT JSON wraps rows in a {"data": [...]} envelope.
    private sealed record Response(List<StoredEvent> Data);

    private sealed record CountResponse(List<CountRow> Data);

    private sealed record CountRow(JsonElement C);
}
