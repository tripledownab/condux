using System.Net;
using Xunit;

namespace Condux.Sdk.Tests;

// Proves where the SDK is loud and where it is quiet: a DSN the client cannot use fails at construction
// (setup time, one clear error), while anything that goes wrong while reporting comes back as a result.
public class ConduxClientTests
{
    private static ConduxClient Client(HttpMessageHandler transport) =>
        new(new ConduxOptions { Dsn = "https://testkey@ingest.example.test/proj-uuid", Transport = transport });

    [Theory]
    [InlineData("https://ingest.example.test/proj-uuid")] // no key: every event would be rejected
    [InlineData("https://testkey@ingest.example.test")] // no project: nothing to route to
    [InlineData("not a dsn at all")]
    public void Rejects_a_dsn_that_is_not_a_whole_dsn(string dsn)
    {
        var error = Assert.Throws<FormatException>(() => new ConduxClient(new ConduxOptions { Dsn = dsn }));

        Assert.Contains("scheme://<key>@<host>/<projectId>", error.Message);
    }

    [Fact]
    public async Task Capture_does_not_throw_when_the_transport_fails_in_an_unexpected_way()
    {
        // A disposed or misbehaving handler throws something the retry loop does not model. Reporting still
        // has to return rather than throw: it runs inside someone else's catch block.
        var transport = new ScriptedTransport(
            () => throw new ObjectDisposedException(nameof(HttpMessageHandler)));

        var result = await Client(transport).CaptureMessageAsync("dropped");

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
    }

    // The exception being reported is application code: its Message is whatever its type decides to
    // compute, and reading it is part of capture. A throw there has to come back as a result too.
    private sealed class HostileException : Exception
    {
        public override string Message => throw new InvalidOperationException("message getter blew up");
    }

    [Fact]
    public async Task Capture_does_not_throw_when_reading_the_exception_itself_fails()
    {
        var transport = new ScriptedTransport(ScriptedTransport.Status(HttpStatusCode.OK));

        var result = await Client(transport).CaptureExceptionAsync(new HostileException());

        Assert.False(result.Ok);
        Assert.Empty(transport.Bodies);
    }

    [Fact]
    public async Task Caller_cancellation_still_surfaces_to_the_caller()
    {
        // Never-throws covers reporting failures, not the caller's own signal — swallowing that would make
        // a cancelled shutdown look like a delivered event.
        var transport = new ScriptedTransport(() => throw new OperationCanceledException());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(transport).CaptureMessageAsync("cancelled", Level.Info, cancellation.Token));
    }
}
