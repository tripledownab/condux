using Condux.Core.RateLimiting;
using Xunit;

namespace Condux.Core.Tests;

public class SpikeGuardTests
{
    [Fact]
    public void ShedsAboveBurst()
    {
        var guard = new SpikeGuard(ratePerSecond: 1, burst: 2, clock: () => 0);
        Assert.True(guard.Check().Allowed);
        Assert.True(guard.Check().Allowed);
        Assert.False(guard.Check().Allowed); // instance-wide budget exhausted
    }

    [Fact]
    public void RecoversAsTimePasses()
    {
        var now = 0.0;
        var guard = new SpikeGuard(ratePerSecond: 1, burst: 1, clock: () => now);
        Assert.True(guard.Check().Allowed);
        Assert.False(guard.Check().Allowed);
        now = 1.0; // one token refills after a second at 1/s
        Assert.True(guard.Check().Allowed);
    }

    [Fact]
    public void ZeroRateDisablesTheGuard()
    {
        var guard = new SpikeGuard(ratePerSecond: 0, burst: 0);
        Assert.True(guard.Disabled);
        for (var i = 0; i < 1_000; i++)
        {
            Assert.True(guard.Check().Allowed);
        }
    }
}
