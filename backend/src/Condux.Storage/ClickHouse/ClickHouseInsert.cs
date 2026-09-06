using System.Text;
using System.Text.Json;

namespace Condux.Storage.ClickHouse;

/// <summary>
/// The one batched insert every ClickHouse writer in this folder uses: serialize the rows as
/// JSONEachRow and POST them. Extracted when a third writer needed the same body, because three
/// identical copies of an HTTP call is how two of them quietly stop matching.
///
/// The <see cref="HttpClient"/> is pre-configured with the base URL, auth headers and resilience by
/// <see cref="ClickHouseRegistration"/>, so nothing here knows where ClickHouse is.
/// </summary>
internal static class ClickHouseInsert
{
    /// <summary>
    /// Insert a batch. An empty batch is a no-op rather than an empty request, since the consumer
    /// flushes on a timer and most ticks have nothing to write.
    /// </summary>
    /// <param name="table">
    /// A fully qualified table name. It is interpolated into the query rather than parameterised,
    /// which is safe here and only here because every caller passes a compile time constant. Never
    /// pass a value that came from a request.
    /// </param>
    public static async Task RowsAsync<TRow>(
        HttpClient http, string table, IReadOnlyList<TRow> rows, CancellationToken cancellationToken)
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

        var url = $"/?query={Uri.EscapeDataString($"INSERT INTO {table} FORMAT JSONEachRow")}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToString(), Encoding.UTF8),
        };

        using var resp = await http.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
    }
}
