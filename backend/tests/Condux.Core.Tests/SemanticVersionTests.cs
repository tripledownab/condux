using Condux.Core.CveScanning;
using Xunit;

namespace Condux.Core.Tests;

public class SemanticVersionTests
{
    /// <summary>
    /// The precedence chains the SemVer 2.0.0 specification gives as its own examples in clause 11,
    /// each in ascending order. Every adjacent pair is checked both ways, so a comparator that answers 0
    /// or always -1 fails.
    /// </summary>
    [Theory]
    [InlineData("1.0.0", "2.0.0", "2.1.0", "2.1.1")]
    [InlineData("1.0.0-alpha", "1.0.0")]
    [InlineData(
        "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11",
        "1.0.0-rc.1", "1.0.0")]
    // Numbers compare as numbers, never as text, up to the largest one npm accepts.
    [InlineData("1.9.0", "1.10.0", "10.0.0", "999999999999999.0.0", "9007199254740991.0.0")]
    public void OrdersByTheSpecsPrecedence(params string[] ascending)
    {
        for (var i = 1; i < ascending.Length; i++)
        {
            var lower = SemanticVersion.Parse(ascending[i - 1])!;
            var higher = SemanticVersion.Parse(ascending[i])!;
            Assert.True(lower.CompareTo(higher) < 0, $"{ascending[i - 1]} < {ascending[i]}");
            Assert.True(higher.CompareTo(lower) > 0, $"{ascending[i]} > {ascending[i - 1]}");
        }
    }

    [Fact]
    public void IgnoresBuildMetadataInPrecedence()
    {
        var plain = SemanticVersion.Parse("1.2.3")!;
        var built = SemanticVersion.Parse("1.2.3+build.0042")!;

        Assert.Equal(0, plain.CompareTo(built));
        Assert.False(built.IsPrerelease);
    }

    /// <summary>
    /// Every loose form a sender might choose is refused rather than read generously, since the caller
    /// turns a refusal into "undecided" and a generous reading into a verdict.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3.4")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-alpha..1")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-al_pha")]
    [InlineData(" 1.2.3")]
    [InlineData("１.2.3")]
    // npm refuses a major, minor or patch past JavaScript's largest safe integer.
    [InlineData("9007199254740992.0.0")]
    [InlineData("1.9007199254740992.0")]
    [InlineData("1.0.10000000000000000")]
    public void RefusesAnythingButAFullVersion(string text) => Assert.Null(SemanticVersion.Parse(text));

    [Fact]
    public void RefusesAVersionLongerThanTheCap()
    {
        var text = "1.2.3-" + new string('a', SemanticVersion.MaxLength);

        Assert.Null(SemanticVersion.Parse(text));
    }

    [Theory]
    [InlineData("0.0.0")]
    [InlineData("1.2.3-0")]
    [InlineData("1.2.3-x-y.0a")]
    [InlineData("1.2.3+001")]
    public void AcceptsTheEdgesTheSpecAllows(string text) => Assert.NotNull(SemanticVersion.Parse(text));
}
