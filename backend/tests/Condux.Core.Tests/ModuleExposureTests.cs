using Condux.Core.CveScanning;
using Xunit;

namespace Condux.Core.Tests;

public class ModuleExposureTests
{
    private static readonly DateTimeOffset SeenAt = new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);

    private static CveFinding Finding(string package, string ecosystem) =>
        new(
            AdvisoryId: "GHSA-jf85-cpcp-j695",
            CveId: "CVE-2019-10744",
            Severity: CveSeverity.Critical,
            Summary: "Prototype pollution",
            Package: package,
            Ecosystem: ecosystem,
            VulnerableRange: "< 4.17.12",
            FixedVersion: "4.17.12",
            Url: "https://github.test/advisories/GHSA-jf85-cpcp-j695",
            Source: "dependabot");

    private static ObservedModule Observed(
        string package, string ecosystem, string version = "4.17.11", string environment = "production") =>
        new(ecosystem, package, version, environment, "1.4.2", SeenAt);

    [Fact]
    public void ReportsRunningWhenAVersionOfTheFindingsPackageWasObserved()
    {
        var exposure = ModuleExposures.For(
            Finding("lodash", "npm"), [Observed("lodash", "npm")]);

        Assert.Equal(ModuleExposureState.Running, exposure.State);
        Assert.Equal("4.17.11", Assert.Single(exposure.Observed).Version);
    }

    /// <summary>
    /// Nothing observed is Unknown, and Unknown is not a quiet "fine". The distinction is the whole
    /// premise of ADR-0041, so it is asserted on the state rather than on the empty list, which a
    /// caller could read either way.
    /// </summary>
    [Fact]
    public void ReportsUnknownRatherThanSafeWhenNothingWasObserved()
    {
        var exposure = ModuleExposures.For(Finding("lodash", "npm"), []);

        Assert.Equal(ModuleExposureState.Unknown, exposure.State);
        Assert.Empty(exposure.Observed);
    }

    /// <summary>
    /// The test the ecosystem fold exists for, and the one that fails without it.
    ///
    /// Dependabot is the only scanner wired to the CVE findings surface and it says "pip" and
    /// "composer"; we record what the SDK's platform maps to, which is OSV's "PyPI" and "Packagist".
    /// Compare those as plain strings and every Python and PHP finding reports "not observed" for
    /// ever, which is indistinguishable from an SDK that never sent a module list. Remove
    /// PackageEcosystems.Canonical from the match and these cases go Unknown.
    /// </summary>
    [Theory]
    [InlineData("pip", "PyPI")]
    [InlineData("composer", "Packagist")]
    [InlineData("rubygems", "RubyGems")]
    [InlineData("nuget", "NuGet")]
    [InlineData("go", "Go")]
    public void MatchesAScannersEcosystemAgainstTheOneWeRecorded(string reported, string recorded)
    {
        var exposure = ModuleExposures.For(
            Finding("requests", reported), [Observed("requests", recorded)]);

        Assert.Equal(ModuleExposureState.Running, exposure.State);
    }

    /// <summary>
    /// A scanner reports the registry's spelling and an SDK reports the runtime's, and PyPI and NuGet
    /// both treat a name case insensitively, so the two legitimately disagree on case for the same
    /// package. Matching ordinally would turn that into a false "not observed".
    /// </summary>
    [Fact]
    public void MatchesThePackageNameCaseInsensitively()
    {
        // Same ecosystem on both sides on purpose, so only the package casing is under test and this
        // cannot pass or fail for the ecosystem fold's reasons.
        var exposure = ModuleExposures.For(
            Finding("Flask", "PyPI"), [Observed("flask", "PyPI", version: "2.0.0")]);

        Assert.Equal(ModuleExposureState.Running, exposure.State);
    }

    /// <summary>
    /// Leniency has a limit. A same-named package in a different ecosystem is a different package, and
    /// reporting it would put someone else's version next to an advisory that has nothing to do with
    /// it.
    /// </summary>
    [Fact]
    public void DoesNotMatchTheSameNameInAnotherEcosystem()
    {
        var exposure = ModuleExposures.For(
            Finding("requests", "pip"), [Observed("requests", "npm")]);

        Assert.Equal(ModuleExposureState.Unknown, exposure.State);
    }

    [Fact]
    public void IgnoresObservationsOfOtherPackages()
    {
        var exposure = ModuleExposures.For(
            Finding("lodash", "npm"),
            [Observed("express", "npm"), Observed("lodash", "npm"), Observed("axios", "npm")]);

        Assert.Equal("lodash", Assert.Single(exposure.Observed).Package);
    }

    /// <summary>
    /// Several versions of one package can be live at once, across releases and environments, and that
    /// spread is the answer rather than noise to collapse: one of them being staging-only changes what
    /// the reader does about it. Order is the reader's, newest sighting first.
    /// </summary>
    [Fact]
    public void KeepsEveryMatchingSightingInTheOrderItWasGiven()
    {
        var exposure = ModuleExposures.For(
            Finding("lodash", "npm"),
            [
                Observed("lodash", "npm", version: "4.17.21"),
                Observed("lodash", "npm", version: "4.17.11", environment: "staging"),
            ]);

        Assert.Equal(ModuleExposureState.Running, exposure.State);
        Assert.Equal(["4.17.21", "4.17.11"], exposure.Observed.Select(module => module.Version));
        Assert.Equal(["production", "staging"], exposure.Observed.Select(module => module.Environment));
    }
}
