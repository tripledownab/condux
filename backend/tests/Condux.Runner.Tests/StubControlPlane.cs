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

    /// <summary>What a dropped report does. A dropped connection by default.</summary>
    public Func<HttpResponseMessage> ReportFailure { get; set; } = () => throw new HttpRequestException("connection reset");

    public List<string> Reports { get; } = [];

    public int HeartbeatCount { get; private set; }

    /// <summary>
    /// How many lease calls time out before answering. HttpClient reports its own timeout as a
    /// <see cref="TaskCanceledException"/> the caller never asked for.
    /// </summary>
    public int TimeOutLeasesBeforeAnswering { get; set; }

    /// <summary>How many heartbeats fail before answering.</summary>
    public int FailHeartbeatsBeforeAnswering { get; set; }

    /// <summary>Fail every other heartbeat, so they keep landing but never two in a row.</summary>
    public bool FailEveryOtherHeartbeat { get; set; }

    private int heartbeatCalls;

    /// <summary>Answer this many heartbeats, then leave every later one hanging until its caller gives up,
    /// as a control plane behind a partition does. Null answers them all.</summary>
    public int? AnswerHeartbeatsThenHang { get; set; }

    /// <summary>When the last answered heartbeat was answered, on <see cref="Clock"/>. It was sent no later.</summary>
    public TimeSpan LastAnsweredHeartbeatAt { get; private set; }

    /// <summary>How long an answered heartbeat waits before its answer, so its send and its answer differ.</summary>
    public TimeSpan HeartbeatAnswerDelay { get; set; }

    /// <summary>When the last answered heartbeat reached the stub, on <see cref="Clock"/>: just after the
    /// runner sent it, since the stub runs in its process. Earlier than <see cref="LastAnsweredHeartbeatAt"/>
    /// by at least <see cref="HeartbeatAnswerDelay"/>.</summary>
    public TimeSpan LastAnsweredHeartbeatSentAt { get; private set; }

    /// <summary>The clock both the stub and a test read, so their times can be compared.</summary>
    public System.Diagnostics.Stopwatch Clock { get; } = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>What a failed heartbeat does: time out, like <see cref="TimeOutLeasesBeforeAnswering"/>, by default.</summary>
    public Func<HttpResponseMessage> HeartbeatFailure { get; set; } =
        () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");

    /// <summary>Completes once a heartbeat is answered.</summary>
    public TaskCompletionSource HeartbeatAnswered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once a lease call is answered, so a test can wait for the worker to get there.</summary>
    public TaskCompletionSource LeaseAnswered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Queue a job for the next lease call. An empty queue answers 204, the ordinary "no work".</summary>
    public void Offer(string json) => leases.Enqueue(json);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;

        if (path.EndsWith("/lease", StringComparison.Ordinal))
        {
            if (TimeOutLeasesBeforeAnswering > 0)
            {
                TimeOutLeasesBeforeAnswering--;
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
            }

            LeaseAnswered.TrySetResult();
            return leases.Count == 0
                ? new HttpResponseMessage(HttpStatusCode.NoContent)
                : Json(leases.Dequeue());
        }

        if (path.EndsWith("/heartbeat", StringComparison.Ordinal))
        {
            var sentAt = Clock.Elapsed;
            heartbeatCalls++;
            if (AnswerHeartbeatsThenHang is { } answered && HeartbeatCount >= answered)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (FailHeartbeatsBeforeAnswering > 0 || (FailEveryOtherHeartbeat && heartbeatCalls % 2 == 1))
            {
                FailHeartbeatsBeforeAnswering = Math.Max(0, FailHeartbeatsBeforeAnswering - 1);
                return HeartbeatFailure();
            }

            if (HeartbeatAnswerDelay > TimeSpan.Zero)
            {
                await Task.Delay(HeartbeatAnswerDelay, cancellationToken);
            }

            HeartbeatAnswered.TrySetResult();
            HeartbeatCount++;
            LastAnsweredHeartbeatSentAt = sentAt;
            LastAnsweredHeartbeatAt = Clock.Elapsed;
            return new HttpResponseMessage(
                LeaseHeld ? HttpStatusCode.NoContent : HttpStatusCode.Conflict);
        }

        if (path.EndsWith("/result", StringComparison.Ordinal))
        {
            if (FailReportsBeforeAccepting > 0)
            {
                FailReportsBeforeAccepting--;
                return ReportFailure();
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
