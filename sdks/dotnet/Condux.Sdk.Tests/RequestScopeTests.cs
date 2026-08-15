using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace Condux.Sdk.Tests;

/// <summary>
/// Request isolation and per-event enrichment.
///
/// The concurrency test runs genuinely parallel work rather than asserting the mechanism in the
/// abstract: the bug it exists to prevent only appears when two requests overlap, and a sequential test
/// passes with a shared scope, proving nothing.
/// </summary>
[Collection("scope")] // the process-level scope is shared state, so these must not interleave with others
public class RequestScopeTests : IDisposable
{
    public void Dispose() => ConduxScope.Clear();

    private static ConduxClient Client(HttpMessageHandler transport) =>
        new(new ConduxOptions { Dsn = "http://pub123@relay.test/7", Transport = transport });

    [Fact]
    public async Task Process_scope_still_applies_when_no_request_is_active()
    {
        // Backward compatibility: a startup SetTag must behave exactly as before request scopes existed.
        ConduxScope.Clear();
        var handler = new RecordingHandler();
        ConduxScope.SetTag("service", "billing");

        await Client(handler).CaptureMessageAsync("hello");

        Assert.Equal("billing", Tags(handler.Last!)["service"]);
    }

    [Fact]
    public async Task Request_scope_layers_over_process_scope()
    {
        ConduxScope.Clear();
        var handler = new RecordingHandler();
        ConduxScope.SetTag("service", "billing");

        using (ConduxScope.BeginRequest())
        {
            ConduxScope.SetTag("tenant", "acme");
            await Client(handler).CaptureMessageAsync("inside");
        }

        var tags = Tags(handler.Last!);
        Assert.Equal("billing", tags["service"]);
        Assert.Equal("acme", tags["tenant"]);
    }

    [Fact]
    public async Task Request_scope_does_not_outlive_the_request()
    {
        ConduxScope.Clear();
        var handler = new RecordingHandler();

        using (ConduxScope.BeginRequest())
        {
            ConduxScope.SetUser(new ConduxUser { Id = "u-1" });
        }
        await Client(handler).CaptureMessageAsync("after");

        // The leak in miniature: without the restore on dispose, the next event carries the previous
        // request's user.
        Assert.False(JsonDocument.Parse(handler.Last!).RootElement.TryGetProperty("user", out _));
    }

    [Fact]
    public async Task Concurrent_requests_do_not_see_each_others_user()
    {
        ConduxScope.Clear();
        var handler = new RecordingHandler();
        var client = Client(handler);
        using var gate = new Barrier(2);

        async Task Handle(string userId)
        {
            using (ConduxScope.BeginRequest())
            {
                ConduxScope.SetUser(new ConduxUser { Id = userId });
                // Both set their user before either captures, so a shared scope means whichever wrote
                // last wins for BOTH events.
                gate.SignalAndWait();
                await client.CaptureMessageAsync(userId);
            }
        }

        await Task.WhenAll(Task.Run(() => Handle("u-1")), Task.Run(() => Handle("u-2")));

        var seen = handler.Bodies
            .Select(body => JsonDocument.Parse(body).RootElement)
            .ToDictionary(
                element => element.GetProperty("message").GetString()!,
                element => element.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal("u-1", seen["u-1"]);
        Assert.Equal("u-2", seen["u-2"]);
    }

    [Fact]
    public async Task Per_event_request_rides_the_wire_in_the_shape_the_relay_parses()
    {
        ConduxScope.Clear();
        var handler = new RecordingHandler();

        await Client(handler).CaptureExceptionAsync(
            new InvalidOperationException("boom"),
            handled: false,
            new CaptureContext
            {
                Request = new ConduxRequest { Url = "/checkout", Method = "POST", QueryString = "step=2" },
            });

        var request = JsonDocument.Parse(handler.Last!).RootElement.GetProperty("request");
        Assert.Equal("/checkout", request.GetProperty("url").GetString());
        Assert.Equal("POST", request.GetProperty("method").GetString());
        // snake_case, because that is the key the relay's parser reads.
        Assert.Equal("step=2", request.GetProperty("query_string").GetString());
    }

    [Fact]
    public async Task An_event_with_no_capture_context_has_no_request_field()
    {
        ConduxScope.Clear();
        var handler = new RecordingHandler();

        await Client(handler).CaptureMessageAsync("plain");

        // Absence, not an empty object: an unenriched event must keep its exact previous wire shape.
        Assert.False(JsonDocument.Parse(handler.Last!).RootElement.TryGetProperty("request", out _));
    }

    private static Dictionary<string, string> Tags(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("tags").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString()!);

    // Records every body, thread-safely, because the concurrency test sends from several tasks at once.
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> bodies = new();

        public IReadOnlyCollection<string> Bodies => bodies;

        public string? Last => bodies.LastOrDefault();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                bodies.Enqueue(await request.Content.ReadAsStringAsync(cancellationToken));
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.Accepted);
        }
    }
}
