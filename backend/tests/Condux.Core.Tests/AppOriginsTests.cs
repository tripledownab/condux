using Condux.Core.Http;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// The deployment's own address, resolved in one place because four callers used to parse it and three
/// of them applied a different policy afterwards. Two of those copies had already drifted: one dropped
/// the trailing-slash trim, and the explicit variable went untrimmed for whitespace long enough to
/// become a way of silently losing the Secure flag on every cookie.
/// </summary>
public class AppOriginsTests
{
    [Fact]
    public void The_explicit_url_wins_over_a_configured_origin()
    {
        Assert.Equal(
            "https://app.condux.ai",
            AppOrigins.ResolveBaseUrl("https://app.condux.ai", "http://localhost:3000"));
    }

    [Fact]
    public void Split_origin_dev_falls_back_to_the_first_configured_origin()
    {
        Assert.Equal(
            "http://localhost:3000",
            AppOrigins.ResolveBaseUrl(null, "http://localhost:3000,http://localhost:3001"));
    }

    /// <summary>
    /// The last case is the one the contract had to be tightened for: a value that trims away to
    /// nothing is absent, not an empty base URL a caller would happily concatenate a path onto.
    /// </summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", null)]
    [InlineData(null, "/")]
    public void Nothing_configured_resolves_to_null_so_each_caller_decides(string? url, string? origins)
    {
        Assert.Null(AppOrigins.ResolveBaseUrl(url, origins));
    }

    /// <summary>
    /// Every consumer concatenates a leading-slash path onto this, so a surviving trailing slash is a
    /// double slash in an invite link, a Stripe return URL and an SSO redirect URI alike.
    /// </summary>
    [Theory]
    [InlineData("https://app.condux.ai/", null)]
    [InlineData(null, "https://app.condux.ai/")]
    public void A_trailing_slash_is_trimmed_from_either_source(string? url, string? origins)
    {
        Assert.Equal("https://app.condux.ai", AppOrigins.ResolveBaseUrl(url, origins));
    }

    /// <summary>
    /// The whitespace case is the one with teeth. An untrimmed value reads correctly to a human in
    /// every log and dashboard, and fails a https:// test, which is how the control-plane decides
    /// whether its cookies carry Secure.
    /// </summary>
    [Fact]
    public void Surrounding_whitespace_is_trimmed_so_the_scheme_stays_readable()
    {
        var resolved = AppOrigins.ResolveBaseUrl("  https://app.condux.ai  ", null);

        Assert.Equal("https://app.condux.ai", resolved);
        Assert.StartsWith("https://", resolved, StringComparison.Ordinal);
    }

    /// <summary>
    /// Order is the invariant that separates this from the email allowlist, which shares the same BCL
    /// call and throws order away. The first entry is the answer, so a parse that reordered would break
    /// split-origin dev while every other assertion here still passed.
    ///
    /// <para>The fixture is deliberately NOT in alphabetical order. Written the obvious way it was, and
    /// a mutation that sorted the list still passed, which made this assertion decorative. Replacing the
    /// array with a set is not covered here and does not need to be: the return type is what callers
    /// index into, so that refactor fails to compile rather than failing silently.</para>
    /// </summary>
    [Fact]
    public void Parse_keeps_the_configured_order_and_trims_each_entry()
    {
        Assert.Equal(
            ["http://zeta.test", "http://alpha.test", "http://mid.test"],
            AppOrigins.Parse(" http://zeta.test , http://alpha.test ,, http://mid.test "));
    }

    /// <summary>
    /// The consequence of the above, stated as the behaviour a caller actually depends on: the base URL
    /// is the FIRST configured origin, not the alphabetically smallest.
    /// </summary>
    [Fact]
    public void The_base_url_is_the_first_origin_not_the_lowest_sorted_one()
    {
        Assert.Equal(
            "http://zeta.test",
            AppOrigins.ResolveBaseUrl(null, "http://zeta.test,http://alpha.test"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(",,")]
    public void Parse_of_nothing_is_empty_rather_than_a_blank_entry(string? value)
    {
        Assert.Empty(AppOrigins.Parse(value));
    }
}
