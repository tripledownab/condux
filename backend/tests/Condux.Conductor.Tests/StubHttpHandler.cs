using System.Net;

namespace Condux.Conductor.Tests;

/// <summary>
/// The one canned-HTTP handler every conductor-side suite stubs vendors with (GitHub, Anthropic, the
/// Managed Agents API): records each request (key, body, headers), answers from fixed routes or
/// per-route sequences, and 404s anything unstubbed. One implementation so the suites cannot drift.
/// </summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    internal sealed record Recorded(string Key, string Body, IReadOnlyDictionary<string, string> Headers);

    public List<Recorded> Requests { get; } = [];
    public Dictionary<string, (HttpStatusCode Status, string Body)> Routes { get; } = [];

    /// <summary>Replies for a route that is called repeatedly, in order — an agentic run posts to the
    /// same messages endpoint every turn, so a single canned response cannot drive it.</summary>
    public Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> Sequences { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = $"{request.Method} {request.RequestUri!.PathAndQuery}";
        // Read now — callers dispose the request (and its content) as soon as their call returns.
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Add(new Recorded(key, body,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value))));

        if (Sequences.TryGetValue(key, out var queued) && queued.Count > 0)
        {
            var (queuedStatus, queuedBody) = queued.Dequeue();
            return new HttpResponseMessage(queuedStatus) { Content = new StringContent(queuedBody) };
        }
        var (status, responseBody) = Routes.TryGetValue(key, out var route)
            ? route
            : (HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
    }
}
