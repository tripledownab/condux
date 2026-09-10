using Condux.Core.Orgs;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// The org name rule and the display slug derived from it. Both used to live in the browser, where the
/// slug was computed and posted, and a name with no ASCII letters produced the empty string. The property
/// that matters here is that a slug is never empty, whatever the name is written in.
/// </summary>
public class OrgNamesTests
{
    [Theory]
    [InlineData("Acme", "acme")]
    [InlineData("Acme Inc", "acme-inc")]
    [InlineData("  Payments API v2!  ", "payments-api-v2")]
    [InlineData("--edge--", "edge")]
    [InlineData("A___B", "a-b")]
    public void ToSlug_lowercases_and_joins_runs_with_single_dashes(string name, string expected) =>
        Assert.Equal(expected, OrgNames.ToSlug(name));

    /// <summary>
    /// The case that reached production. The browser's slugify replaced every character with a dash and
    /// then stripped them, yielding "", which the database accepted once and rejected for ever after,
    /// as a 500. Every one of these names belongs to a plausible customer.
    /// </summary>
    [Theory]
    [InlineData("日本語")]
    [InlineData("Ελλάδα")]
    [InlineData("!!!")]
    [InlineData("...")]
    public void ToSlug_never_returns_empty_for_a_name_with_no_ascii_alphanumerics(string name)
    {
        var slug = OrgNames.ToSlug(name);
        Assert.NotEqual("", slug);
        Assert.DoesNotContain(slug, char.IsWhiteSpace);
    }

    /// <summary>
    /// A slug is not required to be unique and callers must not treat it as though it were: two orgs
    /// legitimately named the same produce the same slug, which is why the column carries no constraint.
    /// </summary>
    [Fact]
    public void ToSlug_is_deterministic_so_two_orgs_of_the_same_name_share_a_slug() =>
        Assert.Equal(OrgNames.ToSlug("Acme"), OrgNames.ToSlug("acme "));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void IsValid_rejects_a_missing_or_blank_name(string? name) =>
        Assert.False(OrgNames.IsValid(name));

    [Fact]
    public void IsValid_rejects_a_name_past_the_cap_and_accepts_one_at_it()
    {
        Assert.True(OrgNames.IsValid(new string('a', OrgNames.MaxLength)));
        Assert.False(OrgNames.IsValid(new string('a', OrgNames.MaxLength + 1)));
    }

    /// <summary>Script is not a validity signal: these are customers, not bad requests.</summary>
    [Theory]
    [InlineData("日本語")]
    [InlineData("Ελλάδα")]
    [InlineData("Æther & Co.")]
    public void IsValid_accepts_a_name_in_any_script(string name) => Assert.True(OrgNames.IsValid(name));
}
