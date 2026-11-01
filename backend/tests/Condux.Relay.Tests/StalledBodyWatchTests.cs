using Condux.Relay.Ingest;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Condux.Relay.Tests;

/// <summary>When <see cref="StalledBodyWatch"/> aborts, driven by a fake clock against a context that
/// records the abort. The watch checks once a second, so the limit is pinned to within a second.</summary>
public class StalledBodyWatchTests
{
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task A_body_that_sends_nothing_is_aborted_once_the_limit_passes()
    {
        var (ctx, lifetime, clock) = Context(new MemoryStream());
        await using var watch = new StalledBodyWatch(ctx, clock);

        clock.Advance(StalledBodyWatch.Limit);
        Assert.False(lifetime.Aborted);
        clock.Advance(Second);
        Assert.True(lifetime.Aborted);
    }

    [Fact]
    public async Task The_limit_runs_from_the_last_byte_read()
    {
        var (ctx, lifetime, clock) = Context(new MemoryStream(new byte[100]));
        await using var watch = new StalledBodyWatch(ctx, clock);

        clock.Advance(StalledBodyWatch.Limit - Second);
        Assert.Equal(10, await watch.Body.ReadAsync(new byte[10]));
        clock.Advance(StalledBodyWatch.Limit);
        Assert.False(lifetime.Aborted);
        clock.Advance(Second);
        Assert.True(lifetime.Aborted);
    }

    [Fact]
    public async Task Nothing_is_aborted_once_the_watch_is_over()
    {
        var (ctx, lifetime, clock) = Context(new MemoryStream());
        var watch = new StalledBodyWatch(ctx, clock);

        await watch.DisposeAsync();
        clock.Advance(StalledBodyWatch.Limit * 3);

        Assert.False(lifetime.Aborted);
    }

    // The context can be torn down under a check that started as the read ended. A timer callback that
    // throws ends the process, so the abort must not.
    [Fact]
    public async Task An_abort_on_a_torn_down_context_does_not_throw()
    {
        var (ctx, lifetime, clock) = Context(new MemoryStream());
        lifetime.ThrowOnAbort = true;
        await using var watch = new StalledBodyWatch(ctx, clock);

        clock.Advance(StalledBodyWatch.Limit * 2);

        Assert.True(lifetime.AbortAttempted);
    }

    private static (HttpContext, RecordingLifetime, FakeTimeProvider) Context(Stream body)
    {
        var lifetime = new RecordingLifetime();
        var ctx = new DefaultHttpContext();
        ctx.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
        ctx.Request.Body = body;
        return (ctx, lifetime, new FakeTimeProvider());
    }

    private sealed class RecordingLifetime : IHttpRequestLifetimeFeature
    {
        public bool Aborted { get; private set; }
        public bool AbortAttempted { get; private set; }
        public bool ThrowOnAbort { get; set; }
        public CancellationToken RequestAborted { get; set; }

        public void Abort()
        {
            AbortAttempted = true;
            if (ThrowOnAbort)
            {
                throw new ObjectDisposedException("IFeatureCollection");
            }
            Aborted = true;
        }
    }
}
