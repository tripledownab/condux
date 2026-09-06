using System.Globalization;
using System.Text.Json;
using Condux.Core.Events;

namespace Condux.Storage.ClickHouse;

/// <summary>One row in the condux.events table (JSON keys match the column names).</summary>
public sealed record EventRow(
    string project_id,
    ulong issue_id,
    string event_id,
    string timestamp,
    string level,
    string platform,
    string environment,
    string release,
    string server_name,
    string transaction,
    string message,
    string exception_type,
    string exception_value,
    string fingerprint,
    Dictionary<string, string> tags,
    string payload,
    int retention_days,
    string user_key);

/// <summary>
/// Writes events to ClickHouse via the HTTP JSONEachRow interface. Insert a batch at a time — the
/// consumer buffers and flushes for cheap, large writes. The <see cref="HttpClient"/> is
/// pre-configured (base URL + auth headers + resilience) by <see cref="ClickHouseRegistration"/>.
/// </summary>
public sealed class ClickHouseEventWriter(HttpClient http)
{
    public Task InsertAsync(IReadOnlyList<EventRow> rows, CancellationToken cancellationToken = default) =>
        ClickHouseInsert.RowsAsync(http, "condux.events", rows, cancellationToken);

    /// <summary>Map a normalized event to a ClickHouse row. <paramref name="retentionDays"/> (the project's
    /// plan-tier retention) drives the row's column-based TTL (migration 0002).</summary>
    public static EventRow ToRow(string projectId, ulong issueId, Event e, string fingerprint, int retentionDays)
    {
        var ex = e.Exceptions.Count > 0 ? e.Exceptions[^1] : null;
        var ms = e.TimestampUnixMs > 0 ? e.TimestampUnixMs : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            .ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

        return new EventRow(
            projectId,
            issueId,
            e.EventId,
            ts,
            e.Level.ToString().ToLowerInvariant(),
            e.Platform ?? "",
            e.Environment ?? "",
            e.Release ?? "",
            e.ServerName ?? "",
            e.Transaction ?? "",
            e.Message ?? "",
            ex?.Type ?? "",
            ex?.Value ?? "",
            fingerprint,
            e.Tags.ToDictionary(kv => kv.Key, kv => kv.Value),
            JsonSerializer.Serialize(e),
            retentionDays,
            e.UserKey);
    }
}
