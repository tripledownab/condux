using Condux.Core.Scrub;
using Xunit;

namespace Condux.Core.Tests;

public class ScrubberTests
{
    [Fact]
    public void RedactsEmail() =>
        Assert.Equal("contact [redacted] now", Scrubber.ScrubString("contact ada@example.com now"));

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
