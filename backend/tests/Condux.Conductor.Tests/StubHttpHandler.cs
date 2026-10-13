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

    /// <summary>Routes matched on method and path alone, for a request whose query a test cannot spell in
    /// advance, such as a lookup by a branch named from an id the run mints. An exact route wins.</summary>
    public Dictionary<string, (HttpStatusCode Status, string Body)> PathRoutes { get; } = [];

    /// <summary>Replies for a route that is called repeatedly, in order — an agentic run posts to the
    /// same messages endpoint every turn, so a single canned response cannot drive it.</summary>
    public Dictionary<string, Queue<(HttpStatusCode Status, string Body)>> Sequences { get; } = [];

    /// <summary>Routes that throw instead of answering, as a timed-out or dropped connection does.</summary>
    public Dictionary<string, Exception> Failures { get; } = [];

    /// <summary>A route that does not answer until released, as a slow model call does, and records
    /// whether its caller gave up waiting.</summary>
    internal sealed class Hold
    {
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool AbandonedByCaller { get; set; }

        /// <summary>Keep waiting through the caller's cancellation, as a step that does not observe it does.</summary>
        public bool IgnoresCancellation { get; init; }
    }

    public Dictionary<string, Hold> Holds { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var key = $"{request.Method} {request.RequestUri!.PathAndQuery}";
        // Read now — callers dispose the request (and its content) as soon as their call returns.
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        // Locked, because a test can drive several runs through one handler at once.
        lock (Requests)
        {
            Requests.Add(new Recorded(key, body,
                request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value))));
        }

        if (Failures.TryGetValue(key, out var failure))
        {
            throw failure;
        }

        if (Holds.TryGetValue(key, out var hold))
        {
            hold.Arrived.TrySetResult();
            try
            {
                await hold.Release.Task.WaitAsync(hold.IgnoresCancellation ? CancellationToken.None : ct);
            }
            catch (OperationCanceledException)
            {
                hold.AbandonedByCaller = true;
                throw;
            }
        }

        if (Sequences.TryGetValue(key, out var queued) && queued.Count > 0)
        {
            var (queuedStatus, queuedBody) = queued.Dequeue();
            return new HttpResponseMessage(queuedStatus) { Content = new StringContent(queuedBody) };
        }
        var (status, responseBody) = Routes.TryGetValue(key, out var route)
            ? route
            : PathRoutes.TryGetValue($"{request.Method} {request.RequestUri.AbsolutePath}", out var pathRoute)
                ? pathRoute
                : (HttpStatusCode.NotFound, """{"message":"Not Found"}""");
        return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
    }
}
