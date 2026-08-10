using Condux.Core.Repos;
using Xunit;

namespace Condux.Core.Tests;

public class CodeMapperTests
{
    private static CodeMapping Mapping(string stackRoot, string sourceRoot) =>
        new(Guid.NewGuid(), Guid.NewGuid(), stackRoot, sourceRoot);

    [Fact]
    public void Resolve_RewritesTheStackRootToTheSourceRoot() =>
        Assert.Equal(
            "src/checkout.js",
            CodeMapper.Resolve("/app/dist/checkout.js", [Mapping("/app/dist/", "src/")]));

    [Fact]
    public void Resolve_EmptySourceRoot_StripsThePrefix() =>
        Assert.Equal(
            "src/checkout.ts",
            CodeMapper.Resolve("webpack:///./src/checkout.ts", [Mapping("webpack:///./", "")]));

    [Fact]
    public void Resolve_LongestMatchingStackRootWins() =>
        Assert.Equal(
            "web/app.js",
            CodeMapper.Resolve(
                "/app/dist/app.js",
                [Mapping("/app/", "root/"), Mapping("/app/dist/", "web/")]));

    [Fact]
    public void Resolve_NoMatchingMapping_ReturnsNull() =>
        Assert.Null(CodeMapper.Resolve("/usr/lib/python3/site.py", [Mapping("/app/", "src/")]));

    [Fact]
    public void Resolve_NoMappings_ReturnsNull() =>
        Assert.Null(CodeMapper.Resolve("/app/dist/x.js", []));
}
