using Condux.ControlPlane.Mcp;
using Xunit;

namespace Condux.ControlPlane.Tests;

/// <summary>
/// The per-token write budget behind the MCP triage tools (ADR-0046). The clock is injected, so refill is
/// exercised without waiting for real seconds.
/// </summary>
public sealed class McpWriteLimiterTests
{
    // A clock the test moves by hand. Every read returns the current value, so reading it twice inside
    // one check cannot advance it.
    private sealed class TestClock
    {
        public double Seconds { get; set; }

        public double Read() => Seconds;
    }

    [Fact]
    public async Task AllowsABurst_ThenRefusesTheNextWrite()
    {
        var clock = new TestClock();
        var limiter = new McpWriteLimiter(clock.Read);
        var token = Guid.NewGuid();

        for (var i = 0; i < McpWriteLimiter.Burst; i++)
        {
            Assert.True(await limiter.TryWriteAsync(token, CancellationToken.None));
        }

        // The budget is spent and no time has passed, so the next call is refused. This is the assertion
        // that fails if the limiter ever short-circuits to always-allow.
        Assert.False(await limiter.TryWriteAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task RefillsOverTime()
    {
        var clock = new TestClock();
        var limiter = new McpWriteLimiter(clock.Read);
        var token = Guid.NewGuid();
        for (var i = 0; i < McpWriteLimiter.Burst; i++)
        {
            await limiter.TryWriteAsync(token, CancellationToken.None);
        }

        clock.Seconds = 1;

        // A refused agent is throttled, never permanently locked out: waiting is the whole remedy.
        Assert.True(await limiter.TryWriteAsync(token, CancellationToken.None));
    }

    [Fact]
    public async Task BudgetsEachTokenSeparately()
    {
        var clock = new TestClock();
        var limiter = new McpWriteLimiter(clock.Read);
        var noisy = Guid.NewGuid();
        for (var i = 0; i <= McpWriteLimiter.Burst; i++)
        {
            await limiter.TryWriteAsync(noisy, CancellationToken.None);
        }

        // One project's runaway agent must not throttle another's, so the key is the token and not a
        // process-wide counter.
        Assert.True(await limiter.TryWriteAsync(Guid.NewGuid(), CancellationToken.None));
    }
}
