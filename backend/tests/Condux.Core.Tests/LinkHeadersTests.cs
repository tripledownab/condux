using Condux.Core.Http;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// The next link read the way RFC 8288 allows a header to be written, not only the way one provider
/// spells it today. A form the reader misses returns null, which a paged read reports as a complete
/// list, so each legal spelling below is a silent undercount if it fails.
/// </summary>
public class LinkHeadersTests
{
    private const string Next = "https://api.github.com/repositories/1/dependabot/alerts?state=open&after=Y3Vy%3D";

    private static string? Find(params string[] values) => LinkHeaders.FindNextUrl(values);

    [Fact]
    public void Reads_the_form_GitHub_sends_today()
    {
        Assert.Equal(Next, Find($"<{Next}>; rel=\"next\""));
    }

    [Fact]
    public void Picks_next_out_of_other_relations_in_any_order()
    {
        Assert.Equal(Next, Find($"<https://x/prev>; rel=\"prev\", <{Next}>; rel=\"next\", <https://x/l>; rel=\"last\""));
    }

    [Theory]
    [InlineData("rel=next")] // a token rather than a quoted string (section 3)
    [InlineData("REL=\"Next\"")] // parameter names and relation types compare case-insensitively
    [InlineData("rel = \"next\"")] // whitespace around the equals sign
    [InlineData("rel=\"prev next\"")] // several relation types in one value (section 3.3)
    [InlineData("title=\"Page 2\"; rel=\"next\"")] // rel need not be the first parameter
    public void Reads_every_legal_spelling_of_next(string parameters)
    {
        Assert.Equal(Next, Find($"<{Next}>; {parameters}"));
    }

    [Fact]
    public void Counts_only_the_first_rel_parameter()
    {
        Assert.Null(Find($"<{Next}>; rel=\"prev\"; rel=\"next\""));
    }

    [Fact]
    public void Keeps_a_target_containing_separators_whole()
    {
        // RFC 3986 allows ";" and "," inside a URI, so the target is cut at its brackets, never at them.
        const string target = "https://api.github.com/x?state=open;a=1,2&after=C";
        Assert.Equal(target, Find($"<{target}>; rel=\"next\""));
    }

    [Fact]
    public void Ignores_separators_inside_a_quoted_parameter()
    {
        Assert.Equal(Next, Find($"<https://x/a>; title=\"a, b; rel=next\", <{Next}>; rel=\"next\""));
    }

    [Fact]
    public void Does_not_read_a_link_out_of_a_quoted_parameter()
    {
        // The quoted title holds text shaped like a next link; only a real link-value may count.
        Assert.Null(Find("<https://x/a>; title=\"a, <https://wrong.example/>; rel=next, b\"; rel=\"prev\""));
    }

    [Fact]
    public void Returns_a_relative_target_as_written_for_the_caller_to_resolve()
    {
        Assert.Equal("/repositories/1/dependabot/alerts?after=C", Find("</repositories/1/dependabot/alerts?after=C>; rel=\"next\""));
    }

    [Fact]
    public void Reads_a_next_link_in_a_second_header_value()
    {
        Assert.Equal(Next, Find("<https://x/p>; rel=\"prev\"", $"<{Next}>; rel=\"next\""));
    }

    [Theory]
    [InlineData("<https://x/p>; rel=\"prev\"")] // the last page carries only prev
    [InlineData("<https://x/n>; rel=\"nextpage\"")] // a different relation type that merely starts with next
    [InlineData("<https://x/n>")] // no rel at all
    [InlineData("rel=\"next\"")] // no target
    [InlineData("")]
    public void Finds_no_next_link_where_there_is_none(string value)
    {
        Assert.Null(Find(value));
    }
}
