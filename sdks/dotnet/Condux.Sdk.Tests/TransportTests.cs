using System.Net;
using Xunit;

namespace Condux.Sdk.Tests;

// Proves delivery is resilient and never throws: retries 429 / 5xx / network errors with backoff, honors
// Retry-After, and stops on a non-retriable status. Backoff is exercised with a recording sleep, no timers.
// Built like the JS transport.test.ts.
public class TransportTests
{
    private const string Dsn = "https://k@ingest.example.test/p";

    private static (ConduxClient Client, List<TimeSpan> Delays) Build(ScriptedTransport transport, int maxRetries = 3)
    {
        var delays = new List<TimeSpan>();
        var client = new ConduxClient(new ConduxOptions
        {
            Dsn = Dsn,
            MaxRetries = maxRetries,
            Transport = transport,
            Sleep = (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
        });
        return (client, delays);
    }

    [Fact]
    public async Task Retries_a_429_honoring_retry_after()
    {
        var transport = new ScriptedTransport(
            ScriptedTransport.RateLimited(retryAfterSeconds: 3), ScriptedTransport.Status(HttpStatusCode.OK));
        var (client, delays) = Build(transport);

        var result = await client.CaptureMessageAsync("hi");

        Assert.True(result.Ok);
        Assert.Equal(2, result.Attempts);
        Assert.Equal([TimeSpan.FromSeconds(3)], delays);
    }

    [Fact]
    public async Task Retries_5xx_with_capped_exponential_backoff()
    {
        var transport = new ScriptedTransport(
            ScriptedTransport.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedTransport.Status(HttpStatusCode.ServiceUnavailable),
            ScriptedTransport.Status(HttpStatusCode.OK));
        var (client, delays) = Build(transport);

        var result = await client.CaptureMessageAsync("hi");

        Assert.True(result.Ok);
        Assert.Equal(3, result.Attempts);
        Assert.Equal([TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400)], delays);
    }

    [Fact]
    public async Task Does_not_retry_a_client_error()
    {
        var transport = new ScriptedTransport(ScriptedTransport.Status(HttpStatusCode.BadRequest));
        var (client, delays) = Build(transport);

        var result = await client.CaptureMessageAsync("hi");

        Assert.False(result.Ok);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(400, result.Status);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task Gives_up_after_max_retries()
    {
        var transport = new ScriptedTransport(ScriptedTransport.Status(HttpStatusCode.ServiceUnavailable));
        var (client, delays) = Build(transport, maxRetries: 2);

        var result = await client.CaptureMessageAsync("hi");

        Assert.False(result.Ok);
        Assert.Equal(3, result.Attempts); // 1 initial + 2 retries
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task Reports_a_network_error_without_throwing()
    {
        var transport = new ScriptedTransport(ScriptedTransport.NetworkError());
        var (client, _) = Build(transport, maxRetries: 1);

        var result = await client.CaptureMessageAsync("hi");

        Assert.False(result.Ok);
        Assert.Equal(2, result.Attempts);
        Assert.Null(result.Status);
        Assert.NotNull(result.Error);
    }
}
