using Condux.Core.CveScanning;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// <see cref="VulnerableRanges.IsConfirmedOutsideRange"/>, which a CVE bump's verification needs before
/// it calls a version fixed. Each expected answer here is what the ecosystem's own tool says: npm's
/// from node-semver 7.8.5, PyPI's from packaging 26.0, Maven's from Maven 3.9.16, Maven 4.0.0-rc-7 and
/// Dependabot. The pip and Maven fixtures hold the same rule over their whole data.
/// </summary>
public class ConfirmedOutsideRangeTests
{
    [Theory]
    // GHSA-29mw-wpgm-hmr9, lodash.
    [InlineData("npm", ">= 4.0.0, < 4.17.21", "4.17.21")]
    [InlineData("npm", ">= 4.0.0, < 4.17.21", "3.10.1")]
    // A partial bound under >= or <, where padding and npm agree (node-semver: 1.2.0 is not "< 1.2").
    [InlineData("npm", "< 1.2", "1.2.0")]
    [InlineData("npm", ">= 1.2", "1.1.9")]
    // GHSA-jj78-5fmv-mv28, express: a release is above its own prerelease bound.
    [InlineData("npm", ">= 3.4.5, < 4.0.0-rc1", "4.0.0")]
    // GHSA-j8r2-6x86-q33q, requests.
    [InlineData("pip", ">= 2.3.0, < 2.31.0", "2.31.0")]
    // A post-release of V is outside "> V", as packaging rejects it.
    [InlineData("pip", "> 1.0, < 2.0", "1.0.post1")]
    // GHSA-jfh8-c2jp-5v3q, log4j-core, the 2.13 line.
    [InlineData("maven", ">= 2.13.0, < 2.15.0", "2.15.0")]
    [InlineData("maven", ">= 2.13.0, < 2.15.0", "2.12.4")]
    // GHSA-5jpm-x58v-624v, netty: ".Final" is the release, so the bound itself is outside "<".
    [InlineData("maven", "< 4.1.108.Final", "4.1.108")]
    public void ConfirmsAVersionOutsideTheRange(string ecosystem, string range, string version) =>
        Assert.True(VulnerableRanges.IsConfirmedOutsideRange(ecosystem, range, version));

    /// <summary>
    /// None of these may be called outside. Some are inside, and the rest are ones the matcher cannot
    /// decide; confirming any of them outside would let a vulnerable version read as fixed.
    /// </summary>
    [Theory]
    // Inside.
    [InlineData("npm", ">= 4.0.0, < 4.17.21", "4.17.20")]
    [InlineData("pip", ">= 2.3.0, < 2.31.0", "2.30.0")]
    [InlineData("maven", ">= 2.13.0, < 2.15.0", "2.14.1")]
    // A partial npm bound under <= or =, which confirming inside pads and npm does not: npm puts 1.2.5
    // inside "<= 1.2" and 12.0.5 inside "= 12.0", so a padded reading would put them outside.
    // Refused, so 1.3.0, which npm does put outside, is undecided too.
    [InlineData("npm", "<= 1.2", "1.2.5")]
    [InlineData("npm", "= 12.0", "12.0.5")]
    [InlineData("npm", "<= 1.2", "1.3.0")]
    // A pre-release on npm or PyPI.
    [InlineData("npm", "< 4.17.21", "4.17.21-beta.1")]
    [InlineData("pip", "< 2.0", "2.0rc1")]
    // A Maven qualifier on a bound's own numbers, which every Maven puts inside this range.
    [InlineData("maven", ">= 2.13.0, < 2.16.0", "2.13.0.redhat-00001")]
    // No scheme, and no range.
    [InlineData("rubygems", "< 2.0.0", "3.0.0")]
    [InlineData("npm", "", "3.0.0")]
    public void LeavesWhatItCannotDecideUnconfirmed(string ecosystem, string range, string version) =>
        Assert.False(VulnerableRanges.IsConfirmedOutsideRange(ecosystem, range, version));

    [Fact]
    public void ConfirmsNothingForAMissingRange() =>
        Assert.False(VulnerableRanges.IsConfirmedOutsideRange("npm", null!, "1.0.0"));
}
