using Condux.Core.Auth;
using Xunit;

namespace Condux.Core.Tests;

public class SessionTokenTests
{
    [Fact]
    public void Create_returns_distinct_raw_tokens()
    {
        Assert.NotEqual(SessionTokens.Create().Raw, SessionTokens.Create().Raw);
    }

    [Fact]
    public void Create_hash_matches_HashToken_of_the_raw_token()
    {
        var (raw, hash) = SessionTokens.Create();
        Assert.Equal(hash, SessionTokens.HashToken(raw));
    }

    [Fact]
    public void Stored_hash_is_not_the_raw_token()
    {
        var (raw, hash) = SessionTokens.Create();
        Assert.NotEqual(raw, hash);
    }

    [Fact]
    public void HashToken_is_deterministic()
    {
        Assert.Equal(SessionTokens.HashToken("token-abc"), SessionTokens.HashToken("token-abc"));
    }

    [Fact]
    public void Raw_token_is_url_safe()
    {
        var raw = SessionTokens.Create().Raw;
        Assert.DoesNotContain('+', raw);
        Assert.DoesNotContain('/', raw);
        Assert.DoesNotContain('=', raw);
    }
}
