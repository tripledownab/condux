using Condux.Core.SourceControl;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// What a repo-relative path may be, and how it is written into a source-host URL. Both halves matter and
/// fail differently: normalizing rejects a traversal loudly, escaping keeps a path that survives from
/// addressing something other than a file. A test here that only covered one half would leave the other
/// free to regress silently.
/// </summary>
public class RepoPathsTests
{
    [Theory]
    [InlineData("src/App.cs", "src/App.cs")]
    [InlineData("README.md", "README.md")]
    [InlineData("  src/App.cs  ", "src/App.cs")]
    [InlineData("./src/App.cs", "src/App.cs")]
    [InlineData("src\\App.cs", "src/App.cs")]
    [InlineData("src/sub.dir/App.cs", "src/sub.dir/App.cs")]
    public void Normalize_accepts_a_repo_relative_path(string path, string expected) =>
        Assert.Equal(expected, RepoPaths.Normalize(path));

    /// <summary>
    /// The traversal cases. Each of these reached an authenticated GitHub REST call, where .NET's Uri
    /// collapses the dot segments before the request goes out, so the request addressed a resource
    /// outside the repository's contents namespace.
    /// </summary>
    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("src/../../other-repo/x")]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("a/../../../b")]
    [InlineData("..\\windows\\style")]
    public void Normalize_refuses_a_path_that_leaves_the_repo_root(string path) =>
        Assert.Throws<ArgumentException>(() => RepoPaths.Normalize(path));

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/")]
    [InlineData("C:/Windows/x")]
    [InlineData("C:\\Windows\\x")]
    [InlineData("https://example.com/x")]
    public void Normalize_refuses_a_rooted_path_or_one_with_a_scheme_or_drive(string path) =>
        Assert.Throws<ArgumentException>(() => RepoPaths.Normalize(path));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Normalize_refuses_an_empty_path(string path) =>
        Assert.Throws<ArgumentException>(() => RepoPaths.Normalize(path));

    /// <summary>
    /// A single dot segment is not a traversal and is left alone, so a legitimate path is not rejected by
    /// a rule aimed at something else. Only a leading "./" is stripped, matching the previous behaviour.
    /// </summary>
    [Fact]
    public void Normalize_keeps_an_interior_single_dot_segment() =>
        Assert.Equal("src/./App.cs", RepoPaths.Normalize("src/./App.cs"));

    [Fact]
    public void IsRepoRelative_answers_without_throwing()
    {
        Assert.True(RepoPaths.IsRepoRelative("src/App.cs"));
        Assert.False(RepoPaths.IsRepoRelative("../escape"));
        Assert.False(RepoPaths.IsRepoRelative("/rooted"));
        Assert.False(RepoPaths.IsRepoRelative(""));
        Assert.False(RepoPaths.IsRepoRelative(null));
    }

    /// <summary>
    /// The separators are structure. Escaping the whole string in one call would encode them and every
    /// nested path in the product would stop resolving, so this is the assertion that keeps the fix from
    /// breaking the feature it protects.
    /// </summary>
    [Fact]
    public void ToUrlPath_leaves_a_plain_nested_path_untouched() =>
        Assert.Equal("src/app/Program.cs", RepoPaths.ToUrlPath("src/app/Program.cs"));

    [Theory]
    [InlineData("src/my file.cs", "src/my%20file.cs")]
    [InlineData("src/a#b.cs", "src/a%23b.cs")]
    [InlineData("src/a?b.cs", "src/a%3Fb.cs")]
    [InlineData("src/a%b.cs", "src/a%25b.cs")]
    [InlineData("src/a&b.cs", "src/a%26b.cs")]
    public void ToUrlPath_encodes_characters_that_would_change_the_request(
        string path, string expected) =>
        Assert.Equal(expected, RepoPaths.ToUrlPath(path));

    /// <summary>
    /// The fact the whole design rests on, pinned against the BCL rather than remembered: <c>.</c> and
    /// <c>..</c> are unreserved in RFC 3986, so percent-encoding a segment leaves a dot segment exactly as
    /// it was. Escaping is therefore no defence against traversal at all, which is why both entry points
    /// reject rather than relying on the encode.
    ///
    /// If this assertion ever fails because the BCL changed, the comments explaining the design are stale
    /// and should be re-checked before anything is relaxed.
    /// </summary>
    [Fact]
    public void Percent_encoding_does_not_touch_a_dot_segment_which_is_why_both_entries_reject()
    {
        Assert.Equal("..", Uri.EscapeDataString(".."));

        Assert.Throws<ArgumentException>(() => RepoPaths.ToUrlPath("../other"));
        Assert.Throws<ArgumentException>(() => RepoPaths.EscapeSegments("../other"));
    }

    /// <summary>
    /// A repository is <c>owner/name</c> and a branch may be <c>feature/x</c>, so both keep their
    /// separators while everything else is encoded.
    /// </summary>
    [Theory]
    [InlineData("tripledownab/condux", "tripledownab/condux")]
    [InlineData("feature/new thing", "feature/new%20thing")]
    [InlineData("owner/repo#1", "owner/repo%231")]
    [InlineData("src/./a.cs", "src/./a.cs")]
    public void EscapeSegments_keeps_separators_and_encodes_the_rest(string value, string expected) =>
        Assert.Equal(expected, RepoPaths.EscapeSegments(value));

    /// <summary>
    /// A repository name and a branch have no repo root to leave, but they do have a URL prefix to leave,
    /// and that is the same attack against a different part of the path. Neither is validated where it is
    /// stored: `RepoEndpoints` writes `req.RepoFullName` and `req.DefaultBranch` with no shape check, so
    /// an org admin decides both. Escaping alone would not help, since `..` is unreserved and survives it.
    ///
    /// Nothing legitimate is refused: `git check-ref-format --branch` rejects every one of these too.
    /// </summary>
    [Theory]
    [InlineData("owner/name/../../other")]
    [InlineData("../../other")]
    [InlineData("feature/../../x")]
    [InlineData("..")]
    // Backslash-separated too. The traversal predicate is shared with Normalize, and when it was written
    // twice the two copies disagreed on exactly this: one converted backslashes first and one did not.
    [InlineData(@"a\..\b")]
    public void EscapeSegments_refuses_a_value_that_traverses_the_url_path(string value) =>
        Assert.Throws<ArgumentException>(() => RepoPaths.EscapeSegments(value));

    /// <summary>
    /// Both entry points answer the same way about traversal, whichever separator carries it. Pins the
    /// shared predicate: if someone re-inlines the check into one of them, this is what notices.
    /// </summary>
    [Theory]
    [InlineData("../x")]
    [InlineData(@"..\x")]
    [InlineData("a/../../b")]
    [InlineData(@"a\..\..\b")]
    public void Both_entry_points_agree_that_a_value_traverses(string value)
    {
        Assert.Throws<ArgumentException>(() => RepoPaths.EscapeSegments(value));
        Assert.Throws<ArgumentException>(() => RepoPaths.Normalize(value));
        Assert.False(RepoPaths.IsRepoRelative(value));
    }
}
