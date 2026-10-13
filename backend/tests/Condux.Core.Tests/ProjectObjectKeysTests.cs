using Condux.Core.SourceMaps;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>Reading a project back out of an object key: what the orphan sweep matches rows on, so a key
/// it misreads is either queued against the wrong project or never looked at.</summary>
public sealed class ProjectObjectKeysTests
{
    [Fact]
    public void A_key_built_from_a_prefix_reads_back_as_its_project()
    {
        foreach (var root in ProjectObjectKeys.Roots)
        {
            Assert.Equal(42, ProjectObjectKeys.ToProjectId(root, ProjectObjectKeys.ToPrefix(root, 42) + "build"));
        }
    }

    [Fact]
    public void A_key_with_more_segments_after_the_project_reads_as_that_project() =>
        Assert.Equal(7, ProjectObjectKeys.ToProjectId(ProjectObjectKeys.SourceMaps, "sourcemaps/7/by-debug-id/abc"));

    [Theory]
    [InlineData("debug-files/7/x")]            // another root
    [InlineData("sourcemaps/abc/x")]           // not a project id
    [InlineData("sourcemaps/-7/x")]            // a sign is not a project id
    [InlineData("sourcemaps/ 7/x")]            // nor is whitespace
    [InlineData("sourcemaps/7")]               // no separator after the project
    [InlineData("sourcemaps/7/")]              // nothing after the project
    [InlineData("sourcemaps//x")]              // an empty project
    [InlineData("sourcemaps/007/x")]           // a project id is never written with a leading zero
    [InlineData("sourcemaps/99999999999999999999/x")] // past a long
    public void A_key_that_does_not_follow_the_layout_has_no_project(string key) =>
        Assert.Null(ProjectObjectKeys.ToProjectId(ProjectObjectKeys.SourceMaps, key));
}
