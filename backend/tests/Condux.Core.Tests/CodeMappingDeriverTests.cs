using Condux.Core.Repos;
using Xunit;

namespace Condux.Core.Tests;

public class CodeMappingDeriverTests
{
    [Fact]
    public void Derives_ThePrefixRewrite_FromASharedFileSuffix()
    {
        var derived = CodeMappingDeriver.Derive(
            ["/app/dist/checkout.js"], ["src/checkout.js", "src/cart.js"]);

        var rule = Assert.Single(derived);
        Assert.Equal("/app/dist/", rule.StackRoot);
        Assert.Equal("src/", rule.SourceRoot);
        Assert.Equal(1, rule.MatchCount);
    }

    [Fact]
    public void AggregatesAcrossFrames_AndCountsSupport()
    {
        var derived = CodeMappingDeriver.Derive(
            ["/app/dist/a.js", "/app/dist/b.js", "/app/dist/lib/c.js"],
            ["src/a.js", "src/b.js", "src/lib/c.js"]);

        var rule = Assert.Single(derived);
        Assert.Equal("/app/dist/", rule.StackRoot);
        Assert.Equal("src/", rule.SourceRoot);
        Assert.Equal(3, rule.MatchCount); // all three frames agree on the same prefix rewrite
    }

    [Fact]
    public void PrefersTheLongestMatchingSuffix_WhenARepoHasNestedCopies()
    {
        // "util/log.js" matches by 2 segments; "packages/app/util/log.js" matches by 3, so the deeper,
        // more specific rewrite wins.
        var derived = CodeMappingDeriver.Derive(
            ["/build/app/util/log.js"], ["util/log.js", "packages/app/util/log.js"]);

        var rule = Assert.Single(derived);
        Assert.Equal("/build/", rule.StackRoot);
        Assert.Equal("packages/", rule.SourceRoot);
    }

    [Fact]
    public void NormalizesWindowsSeparators()
    {
        var derived = CodeMappingDeriver.Derive([@"C:\app\dist\x.js"], ["src/x.js"]);

        var rule = Assert.Single(derived);
        Assert.Equal("C:/app/dist/", rule.StackRoot);
        Assert.Equal("src/", rule.SourceRoot);
    }

    [Fact]
    public void DerivesTheBuildRootRewrite_ForACompiledAppWhoseFramesCarryItsBuildPath()
    {
        // The most common mapping a real project needs, and the one nothing covered: a container image
        // builds under some root, the compiled artifacts record that root, and every runtime frame carries
        // it. The rewrite that fixes it is a bare prefix strip, so the source root is empty.
        var derived = CodeMappingDeriver.Derive(
            [
                "/src/backend/src/Condux.ControlPlane/Program.cs",
                "/src/backend/src/Condux.Storage/Postgres/IssueRepository.cs",
            ],
            [
                "backend/src/Condux.ControlPlane/Program.cs",
                "backend/src/Condux.Storage/Postgres/IssueRepository.cs",
                "web/src/app/page.tsx",
            ]);

        var rule = Assert.Single(derived);
        Assert.Equal("/src/", rule.StackRoot);
        Assert.Equal("", rule.SourceRoot);
        Assert.Equal(2, rule.MatchCount);
    }

    [Fact]
    public void TheDerivedBuildRootRewrite_IsOneCodeMapperCanActuallyApply()
    {
        // The deriver and the resolver have to agree on the leading separator or the suggestion is
        // plausible and inert: it would list in the UI, add cleanly, and silently match no frame.
        const string frame = "/src/backend/src/Condux.ControlPlane/Program.cs";
        var rule = Assert.Single(CodeMappingDeriver.Derive(
            [frame], ["backend/src/Condux.ControlPlane/Program.cs"]));

        var resolved = CodeMapper.Resolve(
            frame, [new CodeMapping(Guid.Empty, Guid.Empty, rule.StackRoot, rule.SourceRoot)]);

        Assert.Equal("backend/src/Condux.ControlPlane/Program.cs", resolved);
    }

    [Fact]
    public void SkipsFramesThatAreAlreadyRepoRelative()
    {
        Assert.Empty(CodeMappingDeriver.Derive(["src/app.js"], ["src/app.js", "src/other.js"]));
    }

    [Fact]
    public void ReturnsNothing_WhenNoFrameMatchesTheTree()
    {
        Assert.Empty(CodeMappingDeriver.Derive(["/app/vendor/x.js"], ["src/y.js", "src/z.js"]));
    }

    [Fact]
    public void RanksMoreStronglySupportedRulesFirst()
    {
        // Two frames support the /dist/ → src/ rewrite, one supports /other/ → lib/; the stronger leads.
        var derived = CodeMappingDeriver.Derive(
            ["/dist/a.js", "/dist/b.js", "/other/c.js"],
            ["src/a.js", "src/b.js", "lib/c.js"]);

        Assert.Equal(2, derived.Count);
        Assert.Equal(("/dist/", "src/", 2), (derived[0].StackRoot, derived[0].SourceRoot, derived[0].MatchCount));
        Assert.Equal(("/other/", "lib/", 1), (derived[1].StackRoot, derived[1].SourceRoot, derived[1].MatchCount));
    }
}
