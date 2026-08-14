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
    public void Assemble_ScopesToTheAlertsOwnManifestDirectory_NotTheRepoRoot()
    {
        // The monorepo case (#41): Dependabot names the workspace member's manifest, and bumping the
        // repo root instead would edit a file the vulnerable dependency is not even declared in.
        var context = CveFixContextAssembler.Assemble(
            "lodash", "npm", "<1", "1", "GHSA-x", null, "", manifestPaths: ["web/package.json"]);

        Assert.Equal("web/package.json", context.ScopedPaths[0]); // the known manifest leads
        Assert.Contains("web/package-lock.json", context.ScopedPaths); // its siblings ride along
        Assert.Contains("web/pnpm-lock.yaml", context.ScopedPaths);
        Assert.DoesNotContain("package.json", context.ScopedPaths); // the root is not touched
        Assert.Contains("web/package.json", context.Prompt); // and the prompt names the location
    }

    [Fact]
    public void Assemble_BumpsEveryAffectedWorkspaceMember_InOneRun()
    {
        // One advisory can raise an alert per workspace member; one run bumps them all rather than
        // fixing the first and leaving the second vulnerable behind a just-fixed finding.
        var context = CveFixContextAssembler.Assemble(
            "lodash", "npm", "<1", "1", "GHSA-x", null, "",
            manifestPaths: ["web/package.json", "api/package.json", "web/package.json"]);

        Assert.Equal("web/package.json", context.ScopedPaths[0]);
        Assert.Equal("api/package.json", context.ScopedPaths[1]); // deduped, order kept
        // Siblings round-robin across members: the gateway fetches only the first few scoped paths and
        // absent candidates burn slots, so all of web/'s candidates before any of api/'s could starve
        // api/'s real lockfile out of the run.
        Assert.Equal("web/package-lock.json", context.ScopedPaths[2]);
        Assert.Equal("api/package-lock.json", context.ScopedPaths[3]);
        Assert.Single(context.ScopedPaths, path => path == "web/package.json");
    }

    [Fact]
    public void Assemble_AKnownManifestMakesAnUnmappedEcosystemFixable()
    {
        // Without a manifest path an unknown ecosystem declines (nothing to edit); with one, the file to
        // edit is known and the ecosystem table not naming it is no reason to refuse the fix.
        var context = CveFixContextAssembler.Assemble(
            "aeson", "haskell", "<1", "1", "GHSA-z", null, "", manifestPaths: ["server/app.cabal"]);

        Assert.Equal(["server/app.cabal"], context.ScopedPaths);
    }

    [Fact]
    public void Assemble_RootManifest_KeepsTheRootCandidateBehavior()
    {
        var context = CveFixContextAssembler.Assemble(
            "lodash", "npm", "<1", "1", "GHSA-x", null, "", manifestPaths: ["package.json"]);

        Assert.Equal("package.json", context.ScopedPaths[0]);
        Assert.Contains("package-lock.json", context.ScopedPaths);
        Assert.Single(context.ScopedPaths, path => path == "package.json");
    }

    [Fact]
    public void EcosystemManifests_MatchesCaseInsensitively()
    {
        Assert.NotEmpty(EcosystemManifests.For("NPM"));
        Assert.Equal(EcosystemManifests.For("npm"), EcosystemManifests.For("Npm"));
    }
}
