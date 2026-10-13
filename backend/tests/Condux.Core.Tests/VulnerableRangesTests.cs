using Condux.Core.CveScanning;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// Every range below is a real npm advisory range, verbatim from GitHub's advisory database, with the
/// advisory it came from. The versions around each are chosen to sit on its bounds, which is where a
/// comparator goes wrong.
/// </summary>
public class VulnerableRangesTests
{
    [Theory]
    // GHSA-29mw-wpgm-hmr9, lodash: an inclusive lower and an exclusive upper bound.
    [InlineData(">= 4.0.0, < 4.17.21", "4.0.0", true)]
    [InlineData(">= 4.0.0, < 4.17.21", "4.17.20", true)]
    [InlineData(">= 4.0.0, < 4.17.21", "4.17.21", false)]
    [InlineData(">= 4.0.0, < 4.17.21", "3.10.1", false)]
    // GHSA-hjrf-2m68-5959, jsonwebtoken: an inclusive upper bound.
    [InlineData("<= 8.5.1", "8.5.1", true)]
    [InlineData("<= 8.5.1", "8.5.2", false)]
    // GHSA-qwph-4952-7xr6, jsonwebtoken.
    [InlineData("< 9.0.0", "8.5.1", true)]
    [InlineData("< 9.0.0", "9.0.0", false)]
    // GHSA-647f-g98j-qq25, vm2: one version only.
    [InlineData("= 3.11.6", "3.11.6", true)]
    [InlineData("= 3.11.6", "3.11.7", false)]
    // GHSA-79rr-5c85-xvw3, openclaw: an exclusive lower bound, and calendar versions.
    [InlineData("> 2026.3.28, < 2026.4.15", "2026.3.28", false)]
    [InlineData("> 2026.3.28, < 2026.4.15", "2026.3.29", true)]
    // GHSA-c2qf-rxjj-qqgw, semver: a prerelease lower bound still orders a release above it.
    [InlineData(">= 2.0.0-alpha, < 5.7.2", "2.0.0", true)]
    [InlineData(">= 2.0.0-alpha, < 5.7.2", "1.9.9", false)]
    // GHSA-jj78-5fmv-mv28, express: a prerelease upper bound excludes the release it precedes.
    [InlineData(">= 3.4.5, < 4.0.0-rc1", "3.21.2", true)]
    [InlineData(">= 3.4.5, < 4.0.0-rc1", "4.0.0", false)]
    // GHSA-qhxv-296x-hjv7: every version, written as a partial bound.
    [InlineData(">= 0", "0.0.1", true)]
    // GHSA-r4jg-5v89-9v62, octocat.js: a partial exclusive upper bound.
    [InlineData("< 1.2", "1.1.9", true)]
    [InlineData("< 1.2", "1.2.0", false)]
    // A partial inclusive bound pads to the narrower reading: npm admits every 1.2.x, this only
    // 1.2.0, so 1.2.5 is left unconfirmed rather than claimed.
    [InlineData("<= 1.2", "1.2.0", true)]
    [InlineData("<= 1.2", "1.2.5", false)]
    [InlineData("= 12.0", "12.0.0", true)]
    // The form the OSV scanner output builds, with no spaces after the operators.
    [InlineData(">=4.0.0 <4.17.21", "4.17.20", true)]
    [InlineData(">=4.0.0 <4.17.21", "4.17.21", false)]
    public void DecidesARealNpmRange(string range, string version, bool expected) =>
        Assert.Equal(expected, VulnerableRanges.IsConfirmedInRange("npm", range, version));

    /// <summary>
    /// Each of these is a version that may well be vulnerable and that the matcher cannot decide, so
    /// it must not be confirmed. Several sit inside the range by plain precedence, which is what makes
    /// them worth pinning: removing a guard turns them true.
    /// </summary>
    [Theory]
    // A prerelease running version. By precedence it is inside, by npm's range rule it is not.
    [InlineData(">= 16.2.0, < 16.3.6", "16.3.0-canary.4")]
    // GHSA-vg35-5wq7-3x7w, tinymce: npm reads "> 0" as ">= 1.0.0", padding as "> 0.0.0", and they
    // disagree on 0.5.0. One bound that cannot be read refuses the whole range, so 5.0.0, inside it
    // either way, is unconfirmed too.
    [InlineData("> 0, <= 5.10.9", "0.5.0")]
    [InlineData("> 0, <= 5.10.9", "5.0.0")]
    // A number past JavaScript's largest safe integer, which npm refuses as a version or a bound.
    [InlineData(">= 0", "9007199254740992.0.0")]
    [InlineData("< 2.0.0", "1.0.99999999999999999999")]
    [InlineData("<= 18446744073709551616.0.0", "3.0.2")]
    // npm reads this as "< 9007199254740992.0.0-0", which passes its ceiling, so it refuses the range.
    [InlineData("<= 9007199254740991", "3.0.3")]
    [InlineData("<= 3.9007199254740991", "0.0.2")]
    // A loose running version.
    [InlineData("< 9.0.0", "v8.5.1")]
    [InlineData("< 9.0.0", "8.5")]
    // A range in a grammar this does not read, or no range at all. The first is admitted by both of
    // its comparators, so it stays false only because the unread "||" refuses the whole range.
    [InlineData("< 9.0.0 || >= 1.0.0", "8.5.1")]
    [InlineData("^8.0.0", "8.5.1")]
    [InlineData("8.5.1", "8.5.1")]
    [InlineData("<", "8.5.1")]
    [InlineData("", "8.5.1")]
    [InlineData("== 8.5.1", "8.5.1")]
    public void LeavesWhatItCannotDecideUnconfirmed(string range, string version) =>
        Assert.False(VulnerableRanges.IsConfirmedInRange("npm", range, version));

    /// <summary>
    /// Only the ecosystems checked against their own advisories may confirm, so every other one
    /// answers false even where SemVer would happen to agree. Removing the ecosystem gate turns these
    /// true.
    /// </summary>
    [Theory]
    [InlineData("rubygems")]
    [InlineData("nuget")]
    [InlineData("go")]
    [InlineData("composer")]
    [InlineData("")]
    public void ConfirmsNothingInAnEcosystemWithNoScheme(string ecosystem) =>
        Assert.False(VulnerableRanges.IsConfirmedInRange(ecosystem, "< 9.0.0", "8.5.1"));

    /// <summary>
    /// <see cref="VulnerableRanges.CanDecide"/> names exactly the ecosystems the matcher dispatches:
    /// "&gt;= 0.0.0" admits 1.0.0 in every scheme, so it is confirmed exactly where there is one.
    /// </summary>
    [Theory]
    [InlineData("npm")]
    [InlineData("pip")]
    [InlineData("PyPI")]
    [InlineData("maven")]
    [InlineData("Maven")]
    [InlineData("rubygems")]
    [InlineData("nuget")]
    [InlineData("go")]
    [InlineData("composer")]
    [InlineData("rust")]
    [InlineData("pub")]
    [InlineData("")]
    public void CanDecideExactlyTheEcosystemsItDispatches(string ecosystem) =>
        Assert.Equal(
            VulnerableRanges.IsConfirmedInRange(ecosystem, ">= 0.0.0", "1.0.0"),
            VulnerableRanges.CanDecide(ecosystem));

    [Fact]
    public void FoldsTheEcosystemSpelling() =>
        Assert.True(VulnerableRanges.IsConfirmedInRange("NPM", "< 9.0.0", "8.5.1"));

    [Fact]
    public void ConfirmsNothingForAMissingRange() =>
        Assert.False(VulnerableRanges.IsConfirmedInRange("npm", null!, "8.5.1"));

    [Fact]
    public void RefusesARangeLongerThanTheCap()
    {
        var range = "< 9.0.0" + new string(' ', SemanticVersion.MaxLength);

        Assert.False(VulnerableRanges.IsConfirmedInRange("npm", range, "8.5.1"));
    }
}
