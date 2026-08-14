using System.Net;
using System.Text;

namespace Condux.Runner.Tests;

/// <summary>
/// A control plane that answers the three lease calls, so the runner can be driven without a server. It
/// records what was reported, because "what did the runner tell us" is the whole observable outcome of a
/// run and the only thing worth asserting on.
/// </summary>
internal sealed class StubControlPlane : HttpMessageHandler
{
    private readonly Queue<string> leases = new();

    /// <summary>Set false to make the next heartbeat fail, which is how a lease is lost mid-run.</summary>
    public bool LeaseHeld { get; set; } = true;

    /// <summary>How many report calls to drop before answering, standing in for a flaky network.</summary>
    public int FailReportsBeforeAccepting { get; set; }

    public List<string> Reports { get; } = [];

    public int HeartbeatCount { get; private set; }

    /// <summary>Queue a job for the next lease call. An empty queue answers 204, the ordinary "no work".</summary>
    public void Offer(string json) => leases.Enqueue(json);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;

        if (path.EndsWith("/lease", StringComparison.Ordinal))
        {
            return leases.Count == 0
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : Json(leases.Dequeue());
        }

        if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
        {
            HeartbeatCount++;
            return new HttpResponseMessage(
                LeaseHeld ? HttpStatusCode.NoContent : HttpStatusCode.Conflict);
        }

        if (path.EndsWith("/result", StringComparison.Ordinal))
        {
            if (FailReportsBeforeAccepting > 0)
            {
                FailReportsBeforeAccepting--;
                throw new HttpRequestException("connection reset");
            }

            Reports.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    public LeaseClient Client() =>
        new(new HttpClient(this) { BaseAddress = new Uri("https://control-plane.test/") });

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
