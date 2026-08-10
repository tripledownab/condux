using Condux.Core.FixEngine;
using Xunit;

namespace Condux.Core.Tests;

public class FixVerificationTests
{
    private static readonly DateTimeOffset Merged = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnyOccurrenceFailsTheFixImmediately()
    {
        Assert.Equal(VerifyStatus.DidNotHold, FixVerification.Evaluate(Merged, Merged.AddHours(1), 1));
        Assert.Equal(VerifyStatus.DidNotHold, FixVerification.Evaluate(Merged, Merged.AddHours(100), 5));
    }

    [Fact]
    public void SilenceInsideTheWindowKeepsWatching()
    {
        Assert.Equal(VerifyStatus.Watching, FixVerification.Evaluate(Merged, Merged.AddHours(1), 0));
        Assert.Equal(VerifyStatus.Watching, FixVerification.Evaluate(Merged, Merged.AddHours(71), 0));
    }

    [Fact]
    public void ASilentFullWindowHolds()
    {
        Assert.Equal(VerifyStatus.Held, FixVerification.Evaluate(Merged, Merged.AddHours(72), 0));
        Assert.Equal(VerifyStatus.Held, FixVerification.Evaluate(Merged, Merged.AddDays(10), 0));
    }
}
