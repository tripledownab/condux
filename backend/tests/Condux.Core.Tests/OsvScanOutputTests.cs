using Condux.Core.CveScanning;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// Parsing osv-scanner's report. The fixture is real output captured from the pinned scanner image
/// against a lockfile with a known-vulnerable dependency, not a hand-written approximation — the wire
/// format is the part that breaks when the tool moves, so a test written from imagination would agree
/// with our assumptions instead of falsifying them.
/// </summary>
public class OsvScanOutputTests
{
    private static string Report() => File.ReadAllText(Path.Combine("Golden", "osv-scanner-output.json"));

    [Fact]
    public void Reads_the_real_report_into_findings()
    {
        var findings = OsvScanOutput.Parse(Report());

        Assert.NotEmpty(findings);
        Assert.All(findings, f => Assert.Equal("lodash", f.Package));
        Assert.All(findings, f => Assert.Equal("npm", f.Ecosystem));
        Assert.All(findings, f => Assert.Equal(OsvScanOutput.SourceName, f.Source));
    }

    [Fact]
    public void Takes_the_severity_from_the_group_score_rather_than_leaving_it_unknown()
    {
        // The score lives only on the group, keyed by advisory id; the detail entry has a CVSS vector
        // string that is not a number. Reading the wrong one would make every finding Unknown.
        var findings = OsvScanOutput.Parse(Report());

        Assert.Contains(findings, f => f.Severity != CveSeverity.Unknown);
    }

    [Fact]
    public void Finds_the_version_to_bump_to()
    {
        // The fix version is buried in affected[].ranges[].events[].fixed, and without it the surface
        // cannot offer a bump at all.
        var fixable = OsvScanOutput.Parse(Report()).Where(f => f.IsFixable).ToList();

        Assert.NotEmpty(fixable);
        Assert.All(fixable, f => Assert.Matches(@"^\d+\.\d+", f.FixedVersion!));
    }

    [Fact]
    public void Carries_the_cve_id_when_the_advisory_has_one()
    {
        var findings = OsvScanOutput.Parse(Report());

        Assert.Contains(findings, f => f.CveId?.StartsWith("CVE-", StringComparison.Ordinal) == true);
        // The advisory id itself stays the GHSA, since that is what the bump run keys on.
        Assert.All(findings, f => Assert.StartsWith("GHSA-", f.AdvisoryId, StringComparison.Ordinal));
    }

    [Fact]
    public void Describes_the_vulnerable_range()
    {
        var findings = OsvScanOutput.Parse(Report());

        Assert.Contains(findings, f => f.VulnerableRange.Contains('<', StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_report_is_no_findings_rather_than_a_failure()
    {
        // A clean repo is the common case and must not look like an error.
        Assert.Empty(OsvScanOutput.Parse("""{"results":[]}"""));
    }

    [Fact]
    public void Malformed_output_throws_instead_of_reporting_a_clean_repo()
    {
        // The dangerous failure: a scanner that changed or died reporting "no vulnerabilities found".
        Assert.ThrowsAny<Exception>(() => OsvScanOutput.Parse("not json"));
    }

    [Fact]
    public void Missing_fields_degrade_rather_than_throw()
    {
        // Advisories vary: no summary, no aliases, no fix. None of that is a parse failure.
        var findings = OsvScanOutput.Parse("""
            {"results":[{"packages":[{"package":{"name":"x","ecosystem":"npm"},
             "vulnerabilities":[{"id":"GHSA-bare"}]}]}]}
            """);

        var finding = Assert.Single(findings);
        Assert.Equal("GHSA-bare", finding.AdvisoryId);
        Assert.Null(finding.CveId);
        Assert.Null(finding.FixedVersion);
        Assert.Equal(CveSeverity.Unknown, finding.Severity);
    }
}
