using System.Net;
using System.Net.Http.Headers;

namespace Condux.Sdk.Tests;

// A stub HttpMessageHandler that replays a scripted sequence of responses (the last one repeats) and
// records every request URL, header set, and body — so tests can assert both the wire shape and the
// retry behavior with no real network or timers.
internal sealed class ScriptedTransport : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> responses;

    public ScriptedTransport(params Func<HttpResponseMessage>[] responses) =>
        this.responses = new Queue<Func<HttpResponseMessage>>(responses);

    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        var next = responses.Count > 1 ? responses.Dequeue() : responses.Peek();
        return next(); // may throw (network-error scripts) — propagates like a real transport failure
    }

    public static Func<HttpResponseMessage> Status(HttpStatusCode status) =>
        () => new HttpResponseMessage(status);

    public static Func<HttpResponseMessage> RateLimited(int retryAfterSeconds) =>
        () => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds)) },
        };

    public static Func<HttpResponseMessage> NetworkError() =>
        () => throw new HttpRequestException("connection refused");
}
