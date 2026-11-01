using Xunit;

namespace Condux.GitHub.Tests;

public class GitHubLinksTests
{
    [Fact]
    public void Links_a_file_at_its_line_on_the_branch() =>
        Assert.Equal(
            "https://github.com/acme/backend/blob/main/src/checkout/cart.ts#L42",
            GitHubLinks.ToBlobUrl("acme/backend", "main", "src/checkout/cart.ts", 42));

    [Fact]
    public void Leaves_out_the_anchor_when_the_line_is_unknown() =>
        Assert.Equal(
            "https://github.com/acme/backend/blob/main/src/a.ts",
            GitHubLinks.ToBlobUrl("acme/backend", "main", "src/a.ts", 0));

    // A browser reads %2e%2e as "..", so an unescaped frame path could leave the repository for any
    // page on github.com. Escaped, it stays a literal segment inside the repository.
    [Fact]
    public void An_encoded_dot_segment_stays_inside_the_repository()
    {
        var url = GitHubLinks.ToBlobUrl(
            "acme/backend", "main", "src/%2e%2e/%2e%2e/%2e%2e/%2e%2e/evil/repo/blob/main/README.md", 1);

        Assert.NotNull(url);
        Assert.StartsWith("/acme/backend/blob/main/src/", new Uri(url).AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public void Escapes_a_query_or_fragment_character_in_the_path() =>
        Assert.Equal(
            "https://github.com/acme/backend/blob/main/src/a%3Fb%23c.ts#L1",
            GitHubLinks.ToBlobUrl("acme/backend", "main", "src/a?b#c.ts", 1));

    [Fact]
    public void Keeps_a_slash_in_a_branch() =>
        Assert.Equal(
            "https://github.com/acme/backend/blob/release/2/src/a.ts#L1",
            GitHubLinks.ToBlobUrl("acme/backend", "release/2", "src/a.ts", 1));

    [Theory]
    [InlineData("acme/backend", "main", "../secrets.txt")]
    [InlineData("acme/backend", "main", "/app/dist/x.js")]
    [InlineData("acme/backend", "main", "webpack:///src/x.ts")]
    [InlineData("acme/backend", "../x", "src/a.ts")]
    [InlineData("../x", "main", "src/a.ts")]
    // A browser reads acme/./blob/main as the repository acme/blob, and . as the branch drops a segment.
    [InlineData("acme/.", "main", "src/a.ts")]
    [InlineData(".", "main", "src/a.ts")]
    [InlineData("acme/backend", ".", "src/a.ts")]
    [InlineData("a//b", "main", "src/a.ts")]
    [InlineData("acme/backend", "", "src/a.ts")]
    public void Refuses_a_path_outside_the_repository_or_a_name_or_branch_that_climbs(
        string repo, string branch, string path) =>
        Assert.Null(GitHubLinks.ToBlobUrl(repo, branch, path, 1));
}
