using Condux.Core.CveScanning;
using Xunit;

namespace Condux.Core.Tests;

public class PackageEcosystemsTests
{
    /// <summary>
    /// Pins the exact OSV spelling of every ecosystem. This is the most important assertion in the
    /// dependency inventory feature, and it looks like the least. OSV answers a query with a wrongly
    /// cased ecosystem by returning zero advisories on HTTP 200, which is byte for byte what it
    /// returns for a package that is genuinely unaffected. A typo here would therefore not break
    /// anything visibly, it would report every dependency as safe.
    /// </summary>
    [Theory]
    [InlineData("javascript", "npm")]
    [InlineData("node", "npm")]
    [InlineData("python", "PyPI")]
    [InlineData("go", "Go")]
    [InlineData("ruby", "RubyGems")]
    [InlineData("php", "Packagist")]
    [InlineData("java", "Maven")]
    [InlineData("csharp", "NuGet")]
    public void MapsEachPlatformToItsOsvEcosystem(string platform, string expected) =>
        Assert.Equal(expected, PackageEcosystems.ToEcosystem(platform));

    /// <summary>
    /// A platform we have no entry for resolves to nothing. These are real values a stock Sentry SDK
    /// reports, and the temptation is to fall back to something plausible; the point of this test is
    /// that there is no fallback, because an absent ecosystem is a knowable unknown and a guessed one
    /// is a confident wrong answer.
    /// </summary>
    [Theory]
    [InlineData("cocoa")]
    [InlineData("native")]
    [InlineData("rust")]
    [InlineData("dart")]
    [InlineData("elixir")]
    [InlineData("")]
    [InlineData(null)]
    public void ReturnsNullForAPlatformWithNoKnownEcosystem(string? platform) =>
        Assert.Null(PackageEcosystems.ToEcosystem(platform));

    /// <summary>
    /// The platform arrives from an SDK and its casing is not ours to control, so the lookup is
    /// case insensitive on the way in. Note the asymmetry that makes this safe: the value we return
    /// is always the canonical OSV spelling, never an echo of what the caller sent.
    /// </summary>
    [Fact]
    public void MatchesThePlatformCaseInsensitivelyButAlwaysAnswersInOsvCasing()
    {
        Assert.Equal("PyPI", PackageEcosystems.ToEcosystem("Python"));
        Assert.Equal("PyPI", PackageEcosystems.ToEcosystem("PYTHON"));
        Assert.Equal("npm", PackageEcosystems.ToEcosystem("JavaScript"));
    }

    /// <summary>
    /// Every ecosystem Dependabot can report, folded to the OSV name for the same thing. Dependabot is
    /// the only scanner wired to the findings surface, so if this fold is wrong the whole feature
    /// reports "not observed" for that language and looks like an SDK that is not sending modules.
    /// The list is GitHub's own enumeration, checked against its REST documentation on 2026-09-04.
    /// </summary>
    [Theory]
    [InlineData("composer", "Packagist")]
    [InlineData("pip", "PyPI")]
    [InlineData("rust", "crates.io")]
    [InlineData("go", "Go")]
    [InlineData("maven", "Maven")]
    [InlineData("npm", "npm")]
    [InlineData("nuget", "NuGet")]
    [InlineData("pub", "Pub")]
    [InlineData("rubygems", "RubyGems")]
    public void FoldsADependabotEcosystemOntoItsOsvName(string dependabot, string expected) =>
        Assert.Equal(expected, PackageEcosystems.Canonical(dependabot));

    /// <summary>
    /// The fold is idempotent, so a finding that already speaks OSV survives it unchanged. Without
    /// this the OSV-sourced scanner would be broken by the fix for the Dependabot-sourced one.
    /// </summary>
    [Theory]
    [InlineData("PyPI")]
    [InlineData("Packagist")]
    [InlineData("RubyGems")]
    [InlineData("NuGet")]
    [InlineData("Maven")]
    [InlineData("Go")]
    [InlineData("npm")]
    public void LeavesAnOsvEcosystemAlone(string osv) =>
        Assert.Equal(osv, PackageEcosystems.Canonical(osv));

    /// <summary>
    /// The output of <see cref="PackageEcosystems.ToEcosystem"/> must survive the fold untouched, or a
    /// module we recorded could never line up with a finding. This is the join the whole feature rests
    /// on, asserted directly rather than inferred from the two tables looking similar.
    /// </summary>
    [Theory]
    [InlineData("javascript")]
    [InlineData("python")]
    [InlineData("go")]
    [InlineData("ruby")]
    [InlineData("php")]
    [InlineData("java")]
    [InlineData("csharp")]
    public void CanonicalIsAFixedPointOfEveryEcosystemWeRecord(string platform)
    {
        var recorded = PackageEcosystems.ToEcosystem(platform);

        Assert.Equal(recorded, PackageEcosystems.Canonical(recorded));
    }

    /// <summary>
    /// An unrecognised name is passed through trimmed rather than dropped or defaulted, so two
    /// spellings we do not know still compare equal to each other and neither is quietly turned into
    /// an ecosystem we do know.
    /// </summary>
    [Fact]
    public void PassesAnUnknownEcosystemThroughInsteadOfGuessing()
    {
        Assert.Equal("swift", PackageEcosystems.Canonical("  swift  "));
        Assert.Equal("", PackageEcosystems.Canonical(null));
    }
}
