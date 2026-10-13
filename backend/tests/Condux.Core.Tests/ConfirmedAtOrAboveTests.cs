using Condux.Core.CveScanning;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// <see cref="VulnerableRanges.IsConfirmedAtOrAbove"/>, the question a CVE bump's verification asks of
/// each running version: has it reached the version the bump targeted? Each minimum is a real
/// advisory's first patched version, and the answer follows that ecosystem's rules, including what
/// each one leaves undecided.
/// </summary>
public class ConfirmedAtOrAboveTests
{
    [Theory]
    // GHSA-29mw-wpgm-hmr9, lodash, first patched 4.17.21.
    [InlineData("npm", "4.17.21", "4.17.21", true)]
    [InlineData("npm", "4.17.21", "4.18.0", true)]
    [InlineData("npm", "4.17.21", "4.17.20", false)]
    // GHSA-j8r2-6x86-q33q, requests, first patched 2.31.0.
    [InlineData("pip", "2.31.0", "2.31.0", true)]
    [InlineData("pip", "2.31.0", "2.32.3+local", true)]
    [InlineData("pip", "2.31.0", "2.30.0", false)]
    // GHSA-jfh8-c2jp-5v3q, log4j-core, first patched 2.15.0.
    [InlineData("maven", "2.15.0", "2.15.0", true)]
    [InlineData("maven", "2.15.0", "2.17.1", true)]
    [InlineData("maven", "2.15.0", "2.14.1", false)]
    // A pre-release of the target is below it: Maven orders it so, and npm and PyPI leave it
    // undecided.
    [InlineData("maven", "2.15.0", "2.15.0-rc1", false)]
    [InlineData("npm", "4.17.21", "4.17.21-beta.1", false)]
    [InlineData("pip", "2.31.0", "2.31.0rc1", false)]
    public void ConfirmsTheTargetWasReached(string ecosystem, string minimum, string version, bool expected) =>
        Assert.Equal(expected, VulnerableRanges.IsConfirmedAtOrAbove(ecosystem, minimum, version));

    /// <summary>Each of these may have reached the target; none can be confirmed, so none is.</summary>
    [Theory]
    // A Maven qualifier on the target's own numbers is not read.
    [InlineData("maven", "2.15.0", "2.15.0.redhat-00001")]
    // An ecosystem with no scheme.
    [InlineData("rubygems", "2.0.0", "3.0.0")]
    // A minimum that is not one version: a comma or a space would add a comparator of its own, here
    // one that admits 2.0.0, and an operator fails to parse as the bound.
    [InlineData("npm", "1.0.0, <= 9.0.0", "2.0.0")]
    [InlineData("npm", "1.0.0 <= 9.0.0", "2.0.0")]
    [InlineData("npm", "1.0.0,<=9.0.0", "2.0.0")]
    [InlineData("npm", "< 5.0.0", "4.0.0")]
    [InlineData("npm", "=1.0.0", "1.0.0")]
    [InlineData("npm", "", "1.0.0")]
    public void LeavesWhatItCannotDecideUnconfirmed(string ecosystem, string minimum, string version) =>
        Assert.False(VulnerableRanges.IsConfirmedAtOrAbove(ecosystem, minimum, version));

    [Fact]
    public void ConfirmsNothingForAMissingMinimum() =>
        Assert.False(VulnerableRanges.IsConfirmedAtOrAbove("npm", null!, "1.0.0"));
}
