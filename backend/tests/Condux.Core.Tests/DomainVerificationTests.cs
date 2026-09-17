using Condux.Core.Auth;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// The rules for proving an org controls the email domain its SSO config claims (ADR-0043). What matters
/// here is that a token is unguessable and per claim, and that a value published in DNS is matched exactly
/// however the resolver chose to present it.
/// </summary>
public class DomainVerificationTests
{
    [Fact]
    public void NewToken_differs_every_time()
    {
        // A token derived from the domain would be the same for everybody, so whoever read it first could
        // publish it and claim a domain they had just learned about. 200 draws is enough to catch a
        // constant or a low-entropy counter, which is the failure this guards.
        var tokens = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            tokens.Add(DomainVerification.NewToken());
        }

        Assert.Equal(200, tokens.Count);
    }

    [Fact]
    public void NewToken_is_safe_to_put_in_a_txt_record()
    {
        var token = DomainVerification.NewToken();

        Assert.DoesNotContain('"', token);
        Assert.DoesNotContain(' ', token);
        Assert.DoesNotContain('=', token);
        Assert.InRange(DomainVerification.RecordValue(token).Length, 1, 255);
    }

    [Fact]
    public void RecordNames_offers_the_apex_first_then_the_challenge_subdomain()
    {
        var names = DomainVerification.RecordNames("acme.test");

        Assert.Equal(["acme.test", "_condux-challenge.acme.test"], names);
    }

    /// <summary>
    /// Both forms are real, not hypothetical: measured on 2026-09-16, Cloudflare returns each TXT value
    /// quoted and Google returns it bare. A matcher that handled only one would verify against one
    /// resolver and silently never verify against the other.
    /// </summary>
    [Theory]
    [InlineData("condux-domain-verification=TOKEN")]
    [InlineData("\"condux-domain-verification=TOKEN\"")]
    public void IsAnswered_accepts_the_value_however_the_resolver_quoted_it(string published) =>
        Assert.True(DomainVerification.IsAnswered([published.Replace("TOKEN", "abc123")], "abc123"));

    [Fact]
    public void IsAnswered_reads_a_value_a_resolver_split_into_chunks()
    {
        // A TXT string is capped at 255 bytes, so a long record arrives as several quoted strings that the
        // reader has to rejoin. Our own value is short, but a zone that puts it beside a long SPF record
        // can still hand back a chunked neighbour, and the matcher must not choke on one.
        var chunked = "\"condux-domain-verification=\" \"abc123\"";

        Assert.True(DomainVerification.IsAnswered([chunked], "abc123"));
    }

    [Fact]
    public void IsAnswered_ignores_the_other_records_in_the_zone()
    {
        string[] values =
        [
            "\"v=spf1 include:_spf.example ~all\"",
            "\"google-site-verification=something-else\"",
            "\"condux-domain-verification=abc123\"",
        ];

        Assert.True(DomainVerification.IsAnswered(values, "abc123"));
    }

    /// <summary>Another org's proof must not answer this org's claim, which is the whole point of the
    /// token being per claim rather than a constant string.</summary>
    [Fact]
    public void IsAnswered_refuses_a_different_claims_token() =>
        Assert.False(DomainVerification.IsAnswered(["\"condux-domain-verification=abc123\""], "xyz789"));

    [Fact]
    public void IsAnswered_refuses_a_value_that_merely_contains_the_token() =>
        Assert.False(DomainVerification.IsAnswered(
            ["\"condux-domain-verification=abc123-and-more\""], "abc123"));

    [Fact]
    public void IsAnswered_refuses_an_empty_token() =>
        Assert.False(DomainVerification.IsAnswered(["\"condux-domain-verification=\""], ""));

    [Fact]
    public void IsAnswered_refuses_a_zone_with_no_records() =>
        Assert.False(DomainVerification.IsAnswered([], "abc123"));
}
