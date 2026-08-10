using Condux.Core.Sampling;
using Xunit;

namespace Condux.Core.Tests;

public class IssueSamplerTests
{
    [Fact]
    public void KeepsFirstNOccurrences()
    {
        var sampler = new IssueSampler(KeepFirst: 3, SampleEvery: 10);
        Assert.True(sampler.ShouldStore(1));
        Assert.True(sampler.ShouldStore(2));
        Assert.True(sampler.ShouldStore(3));
        Assert.False(sampler.ShouldStore(4)); // past KeepFirst, not yet a sample point
    }

    [Fact]
    public void SamplesRepeatsOneInM()
    {
        var sampler = new IssueSampler(KeepFirst: 0, SampleEvery: 5);
        Assert.False(sampler.ShouldStore(1));
        Assert.False(sampler.ShouldStore(4));
        Assert.True(sampler.ShouldStore(5));
        Assert.False(sampler.ShouldStore(9));
        Assert.True(sampler.ShouldStore(10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void SampleEveryOneOrZeroKeepsEverything(long every)
    {
        var sampler = new IssueSampler(KeepFirst: 0, SampleEvery: every);
        for (long i = 1; i <= 50; i++)
        {
            Assert.True(sampler.ShouldStore(i));
        }
    }

    [Fact]
    public void UnlimitedNeverSheds()
    {
        for (long i = 1; i <= 1_000; i++)
        {
            Assert.True(IssueSampler.Unlimited.ShouldStore(i));
        }
    }
}
