using System.Diagnostics;
using Condux.Core.Scrub;
using Xunit;

namespace Condux.Core.Tests;

public class ScrubberTests
{
    [Fact]
    public void RedactsEmail() =>
        Assert.Equal("contact [redacted] now", Scrubber.ScrubString("contact ada@example.com now"));

    // Scrubbing runs on the ingest hot path over strings the sender chooses, bounded only by the body
    // ceiling, so it has to stay linear in their length. The email pattern is ambiguous on paper (its
    // domain class also matches the "." that follows it) and is safe only because the engine anchors on
    // the "@" rather than retrying every start position. That is a property of the pattern plus the
    // engine, not of the code around it, so pin it: an edit that reintroduces the ambiguity, or a runtime
    // that stops optimising it, fails here. It runs in about a millisecond, so the bound is loose.
    [Fact]
    public void ScrubsAPathologicalNearEmailInLinearTime()
    {
        var input = new string('a', 100_000) + "@" + new string('b', 100_000);

        var clock = Stopwatch.StartNew();
        var scrubbed = Scrubber.ScrubString(input);
        clock.Stop();

        Assert.Equal(input, scrubbed);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"scrubbing took {clock.Elapsed}");
    }

    [Fact]
    public void RedactsToken() =>
        Assert.Equal("key [redacted] end", Scrubber.ScrubString("key sk-ABCDEF0123456789 end"));

    [Theory]
    [InlineData("Authorization", true)]
    [InlineData("X-Api-Key", true)]
    [InlineData("password", true)]
    [InlineData("user_id", false)]
    public void FlagsSensitiveKeys(string key, bool expected) =>
        Assert.Equal(expected, Scrubber.IsSensitiveKey(key));
}
