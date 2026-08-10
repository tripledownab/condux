using Condux.Core.CveFix;
using Xunit;

namespace Condux.Core.Tests;

public class CveFixContextAssemblerTests
{
    [Fact]
    public void Assemble_BuildsABumpPrompt_NamingThePackageVersionsAndCve()
    {
        var context = CveFixContextAssembler.Assemble(
            "lodash", "npm", "< 4.17.12", "4.17.12", "GHSA-jf85-cpcp-j695", "CVE-2019-10744",
            "Prototype pollution in lodash");

        Assert.Contains("lodash", context.Prompt);
        Assert.Contains("< 4.17.12", context.Prompt);
        Assert.Contains("4.17.12", context.Prompt);
        Assert.Contains("CVE-2019-10744", context.Prompt); // the CVE id is preferred over the GHSA id
        Assert.Contains("Prototype pollution in lodash", context.Prompt);
    }

    [Fact]
    public void Assemble_FallsBackToTheGhsaId_WhenNoCveIsAssigned()
    {
        var context = CveFixContextAssembler.Assemble(
            "left-pad", "npm", "< 1.3.0", "1.3.0", "GHSA-aaaa-bbbb-cccc", cveId: null, summary: "");

        Assert.Contains("GHSA-aaaa-bbbb-cccc", context.Prompt);
    }

    [Fact]
    public void Assemble_ScopesToTheEcosystemsManifestCandidates()
    {
        var npm = CveFixContextAssembler.Assemble("lodash", "npm", "<1", "1", "GHSA-x", null, "");
        Assert.Contains("package.json", npm.ScopedPaths);
        Assert.Contains("package-lock.json", npm.ScopedPaths);

        var pip = CveFixContextAssembler.Assemble("requests", "pip", "<1", "1", "GHSA-y", null, "");
        Assert.Contains("requirements.txt", pip.ScopedPaths);
        Assert.Contains("pyproject.toml", pip.ScopedPaths);
    }

    [Fact]
    public void Assemble_ReturnsNoManifests_ForAnUnsupportedEcosystem()
    {
        var context = CveFixContextAssembler.Assemble("thing", "haskell", "<1", "1", "GHSA-z", null, "");
        Assert.Empty(context.ScopedPaths);
    }

    [Fact]
    public void EcosystemManifests_MatchesCaseInsensitively()
    {
        Assert.NotEmpty(EcosystemManifests.For("NPM"));
        Assert.Equal(EcosystemManifests.For("npm"), EcosystemManifests.For("Npm"));
    }
}
