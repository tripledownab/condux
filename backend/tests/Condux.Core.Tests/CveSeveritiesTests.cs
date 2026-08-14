using Condux.Core.CveScanning;
using Xunit;

namespace Condux.Core.Tests;

public class CveSeveritiesTests
{
    private static CveFinding Finding(
        string package, string advisory, CveSeverity severity = CveSeverity.High, string? fixedVersion = "2.0.0") =>
        new(advisory, null, severity, "summary", package, "npm", "<2.0.0", fixedVersion, "https://x.test", "test");

    [Theory]
    [InlineData("critical", CveSeverity.Critical)]
    [InlineData("HIGH", CveSeverity.High)]
    [InlineData("low", CveSeverity.Low)]
    public void Reads_the_severity_words_a_source_uses(string raw, CveSeverity expected)
    {
        Assert.Equal(expected, CveSeverities.Parse(raw));
    }

    [Fact]
    public void Treats_moderate_and_medium_as_the_same_band()
    {
        // Dependabot says "moderate", CVSS qualitative says "medium". Splitting them would sort the same
        // vulnerability differently depending on which scanner found it.
        Assert.Equal(CveSeverity.Moderate, CveSeverities.Parse("moderate"));
        Assert.Equal(CveSeverity.Moderate, CveSeverities.Parse("medium"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("severe")]
    public void An_unrecognized_severity_is_unknown_rather_than_a_guess(string? raw)
    {
        // Guessing would sort a misread word against real criticals, which is worse than admitting we
        // cannot tell.
        Assert.Equal(CveSeverity.Unknown, CveSeverities.Parse(raw));
    }

    [Theory]
    [InlineData(9.8, CveSeverity.Critical)]
    [InlineData(7.0, CveSeverity.High)]
    [InlineData(5.4, CveSeverity.Moderate)]
    [InlineData(0.1, CveSeverity.Low)]
    [InlineData(0.0, CveSeverity.Unknown)]
    public void Maps_a_cvss_score_to_its_defined_band(double score, CveSeverity expected)
    {
        Assert.Equal(expected, CveSeverities.FromCvssScore(score));
    }

    [Fact]
    public void Collapses_the_same_advisory_reported_twice_for_one_package()
    {
        // Two scanners, or one scanner seeing the package in two lockfiles.
        var normalized = CveSeverities.Normalize(
            [Finding("lodash", "GHSA-1"), Finding("lodash", "GHSA-1")]);

        Assert.Single(normalized);
    }

    [Fact]
    public void Keeps_one_advisory_that_affects_several_packages()
    {
        // Deduping on the advisory alone would silently hide a second vulnerable package.
        var normalized = CveSeverities.Normalize(
            [Finding("lodash", "GHSA-1"), Finding("underscore", "GHSA-1")]);

        Assert.Equal(2, normalized.Count);
    }

    [Fact]
    public void Prefers_the_duplicate_that_names_a_fix()
    {
        // A finding you can act on beats an identical one you cannot, whichever arrived first.
        var normalized = CveSeverities.Normalize(
        [
            Finding("lodash", "GHSA-1", fixedVersion: null),
            Finding("lodash", "GHSA-1", fixedVersion: "4.17.21"),
        ]);

        Assert.Equal("4.17.21", Assert.Single(normalized).FixedVersion);
    }

    [Fact]
    public void Orders_worst_first_so_the_surface_leads_with_what_matters()
    {
        var normalized = CveSeverities.Normalize(
        [
            Finding("a", "GHSA-1", CveSeverity.Low),
            Finding("b", "GHSA-2", CveSeverity.Critical),
            Finding("c", "GHSA-3", CveSeverity.Moderate),
        ]);

        Assert.Equal(
            [CveSeverity.Critical, CveSeverity.Moderate, CveSeverity.Low],
            normalized.Select(f => f.Severity));
    }

    [Fact]
    public void A_finding_without_a_fixed_version_is_not_fixable()
    {
        // The bump run has nothing to bump to, so the surface must not offer the action.
        Assert.False(Finding("lodash", "GHSA-1", fixedVersion: null).IsFixable);
        Assert.False(Finding("lodash", "GHSA-1", fixedVersion: "  ").IsFixable);
        Assert.True(Finding("lodash", "GHSA-1").IsFixable);
    }
}
