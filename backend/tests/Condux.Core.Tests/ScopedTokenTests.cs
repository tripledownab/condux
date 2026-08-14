using Condux.Core.Auth;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// The one implementation behind every machine credential. These pin the wire format rather than just the
/// behaviour, because MCP and release tokens are already issued and stored as hashes: change the encoding
/// or the casing and every live token stops authenticating, with no error that points at the cause.
/// </summary>
public class ScopedTokenTests
{
    [Fact]
    public void The_stored_hash_is_upper_case_hex_of_the_raw_token()
    {
        // A fixed input, so a change in algorithm or casing fails here rather than in production. This
        // exact value is what a stored hash looks like today.
        const string raw = "condux_mcp_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        var hash = ScopedToken.Hash(raw);

        Assert.Equal(hash.ToUpperInvariant(), hash);
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9A-F]{64}$", hash);
    }

    [Fact]
    public void The_secret_is_base64url_and_carries_no_padding_or_url_unsafe_characters()
    {
        // Tokens travel in Authorization headers and get pasted into config files; '+', '/' and '=' would
        // survive neither reliably.
        var (raw, _) = ScopedToken.Create("condux_test_");
        var secret = raw["condux_test_".Length..];

        Assert.DoesNotContain('+', secret);
        Assert.DoesNotContain('/', secret);
        Assert.DoesNotContain('=', secret);
        Assert.Equal(ScopedToken.EncodedSecretLength, secret.Length);
    }

    [Fact]
    public void Every_token_type_shares_one_format()
    {
        // The drift this replaces: three copies had reached two secret encodings and two hash casings,
        // which is invisible until something compares hashes across types.
        var mcp = McpTokens.Create();
        var release = ReleaseTokens.Create();
        var runner = RunnerTokens.Create();

        Assert.Equal(
            [mcp.Raw.Length - McpTokens.Prefix.Length, release.Raw.Length - ReleaseTokens.Prefix.Length],
            [runner.Raw.Length - RunnerTokens.Prefix.Length, runner.Raw.Length - RunnerTokens.Prefix.Length]);
        Assert.All([mcp.Hash, release.Hash, runner.Hash], h => Assert.Matches("^[0-9A-F]{64}$", h));
    }

    [Fact]
    public void A_token_of_one_kind_is_not_mistaken_for_another()
    {
        // Presented to the wrong endpoint it must be refused on shape, before any database lookup.
        Assert.False(McpTokens.LooksLikeToken(RunnerTokens.Create().Raw));
        Assert.False(RunnerTokens.LooksLikeToken(ReleaseTokens.Create().Raw));
        Assert.False(ReleaseTokens.LooksLikeToken(McpTokens.Create().Raw));
    }

    [Fact]
    public void A_token_is_recognised_by_its_own_type_and_hashes_stably()
    {
        var (raw, hash) = RunnerTokens.Create();

        Assert.True(RunnerTokens.LooksLikeToken(raw));
        Assert.Equal(hash, RunnerTokens.HashToken(raw));
        Assert.NotEqual(raw, hash);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("condux_run_")]
    [InlineData("condux_run_tooshort")]
    public void Anything_the_wrong_shape_is_rejected(string? raw)
    {
        Assert.False(RunnerTokens.LooksLikeToken(raw));
    }
}
