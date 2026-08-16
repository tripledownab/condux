using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Condux.Sdk.Tests;

// Proves the emitted JSON is the Sentry store wire shape the relay parses (field by field), so a Condux
// .NET SDK event normalizes exactly like an official Sentry SDK. Mirrors the JS event.test.ts.
public class EventPayloadTests
{
    private const string Dsn = "https://testkey@ingest.example.test/proj-uuid";

    private static ConduxClient Client(ScriptedTransport transport, string? environment = null, string? release = null) =>
        new(new ConduxOptions
        {
            Dsn = Dsn,
            Environment = environment,
            Release = release,
            Transport = transport,
            Sleep = (_, _) => Task.CompletedTask,
        });

    private static JsonElement LastBody(ScriptedTransport transport) =>
        JsonDocument.Parse(transport.Bodies[^1]).RootElement;

    [Fact]
    public async Task Captures_an_exception_in_the_sentry_store_shape()
    {
        var transport = new ScriptedTransport(ScriptedTransport.Status(HttpStatusCode.OK));
        var result = await Client(transport, environment: "test", release: "1.2.3")
            .CaptureExceptionAsync(Acme.Checkout.OrderService.Thrown());

        Assert.True(result.Ok);
        var body = LastBody(transport);
        Assert.Matches(new Regex("^[0-9a-f]{32}$"), body.GetProperty("event_id").GetString());
        Assert.True(body.GetProperty("timestamp").GetDouble() > 0);
        Assert.Equal("csharp", body.GetProperty("platform").GetString());
        Assert.Equal("error", body.GetProperty("level").GetString());
        Assert.Equal("test", body.GetProperty("environment").GetString());
        Assert.Equal("1.2.3", body.GetProperty("release").GetString());

        var exception = body.GetProperty("exception").GetProperty("values")[0];
        Assert.EndsWith("InvalidOperationException", exception.GetProperty("type").GetString());
        Assert.Equal("boom from the sdk", exception.GetProperty("value").GetString());
        // A handled capture carries the Sentry mechanism (drives the unhandled badge).
        var mechanism = exception.GetProperty("mechanism");
        Assert.Equal("generic", mechanism.GetProperty("type").GetString());
        Assert.True(mechanism.GetProperty("handled").GetBoolean());

        var frames = exception.GetProperty("stacktrace").GetProperty("frames");
        Assert.True(frames.GetArrayLength() > 0);
        // The crashing frame is last (oldest-first order); it is our in-app throwing method.
        var top = frames[frames.GetArrayLength() - 1];
        Assert.Contains("Thrown", top.GetProperty("function").GetString());
        Assert.True(top.GetProperty("in_app").GetBoolean());

        // Auth + routing.
        var request = transport.Requests[^1];
        Assert.Equal("https://ingest.example.test/api/proj-uuid/store/", request.RequestUri!.ToString());
        Assert.Equal("testkey", request.Headers.GetValues("x-condux-auth").Single());
    }

    [Fact]
    public async Task Captures_a_message_at_the_given_level_without_an_exception()
    {
        var transport = new ScriptedTransport(ScriptedTransport.Status(HttpStatusCode.OK));

        await Client(transport).CaptureMessageAsync("disk almost full", Level.Warning);

        var body = LastBody(transport);
        Assert.Equal("warning", body.GetProperty("level").GetString());
        Assert.Equal("disk almost full", body.GetProperty("message").GetString());
        Assert.False(body.TryGetProperty("exception", out _));
    }

    [Fact]
    public async Task Omits_environment_release_and_stacktrace_when_absent()
    {
        var transport = new ScriptedTransport(ScriptedTransport.Status(HttpStatusCode.OK));

        // A never-thrown exception has no stack, so the stacktrace is omitted rather than faked.
        await Client(transport).CaptureExceptionAsync(new InvalidOperationException("no stack"));

        var body = LastBody(transport);
        Assert.False(body.TryGetProperty("environment", out _));
        Assert.False(body.TryGetProperty("release", out _));
        var exception = body.GetProperty("exception").GetProperty("values")[0];
        Assert.Equal("no stack", exception.GetProperty("value").GetString());
        Assert.False(exception.TryGetProperty("stacktrace", out _));
    }

    [Fact]
    public async Task Can_mark_an_exception_unhandled()
    {
        // A framework integration reports uncaught exceptions with handled: false, driving the badge.
        var transport = new ScriptedTransport(ScriptedTransport.Status(HttpStatusCode.OK));
        await Client(transport).CaptureExceptionAsync(Acme.Checkout.OrderService.Thrown(), handled: false);

        var mechanism = LastBody(transport)
            .GetProperty("exception").GetProperty("values")[0].GetProperty("mechanism");
        Assert.False(mechanism.GetProperty("handled").GetBoolean());
    }

    // in_app drives the relay's grouping fingerprint, the culprit, and which files the fix engine follows
    // back to a repository, so a frame wrongly marked in-app is not cosmetic.
    //
    // Condux.Sdk.* is excluded for the same reason the Python SDK excludes its own directory: this SDK's
    // ASP.NET middleware sits in the request pipeline, so its frame is in EVERY unhandled request
    // exception it reports, and leaving it in-app put our own plumbing in the customer's fingerprint.
    [Fact]
    public async Task ThisSdksOwnFramesAreNotInApp()
    {
        var transport = new ScriptedTransport(ScriptedTransport.Status(HttpStatusCode.OK));
        var client = Client(transport);

        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException error)
        {
            await client.CaptureExceptionAsync(error);
        }

        var frames = LastBody(transport)
            .GetProperty("exception").GetProperty("values")[0]
            .GetProperty("stacktrace").GetProperty("frames");

        for (var i = 0; i < frames.GetArrayLength(); i++)
        {
            var function = frames[i].GetProperty("function").GetString() ?? "";
            if (function.StartsWith("Condux.Sdk", StringComparison.Ordinal))
            {
                Assert.False(frames[i].GetProperty("in_app").GetBoolean(),
                    $"{function} is this SDK's own frame and must not be in-app");
            }
        }
    }

}
