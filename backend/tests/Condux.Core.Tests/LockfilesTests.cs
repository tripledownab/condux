using Condux.Core.CveScanning;
using Xunit;

namespace Condux.Core.Tests;

public class LockfilesTests
{
    [Theory]
    [InlineData("package-lock.json")]
    [InlineData("web/pnpm-lock.yaml")]
    [InlineData("services/api/go.mod")]
    [InlineData("Gemfile.lock")]
    public void Recognizes_a_lockfile_wherever_it_sits(string path)
    {
        Assert.True(Lockfiles.IsLockfile(path));
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("src/package.json")]
    [InlineData("lockfile.txt")]
    public void Ignores_everything_else(string path)
    {
        // package.json in particular: it states a range, while package-lock.json states what is installed.
        Assert.False(Lockfiles.IsLockfile(path));
    }

    [Fact]
    public void Skips_dependencies_that_are_not_the_projects_own()
    {
        // A vendored copy or a test fixture reports real vulnerabilities nobody ships, which is how a
        // security surface earns being ignored.
        var selected = Lockfiles.SelectFrom(
        [
            "package-lock.json",
            "node_modules/foo/package-lock.json",
            "vendor/bar/Gemfile.lock",
            "testdata/composer.lock",
            "sdks/go/testdata/go.mod",
        ]);

        Assert.Equal(["package-lock.json"], selected);
    }

    [Fact]
    public void Prefers_the_shallowest_when_it_has_to_truncate()
    {
        // A repository's own lockfiles sit near the root; deep ones are usually examples. A truncated scan
        // should keep what ships.
        var deep = Enumerable.Range(0, Lockfiles.MaxPerScan + 10)
            .Select(i => $"a/b/c/d/e/pkg{i}/package-lock.json");

        var selected = Lockfiles.SelectFrom([.. deep, "package-lock.json"]);

        Assert.Equal(Lockfiles.MaxPerScan, selected.Count);
        Assert.Equal("package-lock.json", selected[0]);
    }

    [Fact]
    public void Returns_nothing_for_a_repository_with_no_dependencies_to_scan()
    {
        Assert.Empty(Lockfiles.SelectFrom(["README.md", "src/main.c"]));
    }
}
