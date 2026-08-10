using Condux.Core.Auth;
using Xunit;

namespace Condux.Core.Tests;

public sealed class McpTokensTests
{
    [Fact]
    public void Create_ProducesAPrefixedRawTokenWhoseHashRoundTrips()
    {
        var (raw, hash) = McpTokens.Create();

        Assert.StartsWith(McpTokens.Prefix, raw);
        // The store keeps the hash, never the raw token; hashing the presented token reproduces it.
        Assert.Equal(hash, McpTokens.HashToken(raw));
        Assert.NotEqual(raw, hash);
        Assert.Equal(64, hash.Length); // SHA-256, hex
    }

    [Fact]
    public void Create_ProducesUniqueTokens()
    {
        var (a, _) = McpTokens.Create();
        var (b, _) = McpTokens.Create();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void HashToken_IsStableAndDistinguishesDifferentTokens()
    {
        Assert.Equal(McpTokens.HashToken("condux_mcp_abc"), McpTokens.HashToken("condux_mcp_abc"));
        Assert.NotEqual(McpTokens.HashToken("condux_mcp_abc"), McpTokens.HashToken("condux_mcp_abd"));
    }
}
