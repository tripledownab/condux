using Condux.Core.Auth;
using Xunit;

namespace Condux.Core.Tests;

public sealed class ReleaseTokensTests
{
    [Fact]
    public void Create_ProducesAPrefixedRawTokenWhoseHashRoundTrips()
    {
        var (raw, hash) = ReleaseTokens.Create();

        Assert.StartsWith(ReleaseTokens.Prefix, raw);
        // The store keeps the hash, never the raw token; hashing the presented token reproduces it.
        Assert.Equal(hash, ReleaseTokens.HashToken(raw));
        Assert.NotEqual(raw, hash);
        Assert.Equal(64, hash.Length); // SHA-256, hex
    }

    [Fact]
    public void Create_ProducesUniqueTokens()
    {
        var (a, _) = ReleaseTokens.Create();
        var (b, _) = ReleaseTokens.Create();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void HashToken_IsStableAndDistinguishesDifferentTokens()
    {
        Assert.Equal(ReleaseTokens.HashToken("condux_rel_abc"), ReleaseTokens.HashToken("condux_rel_abc"));
        Assert.NotEqual(ReleaseTokens.HashToken("condux_rel_abc"), ReleaseTokens.HashToken("condux_rel_abd"));
    }
}
