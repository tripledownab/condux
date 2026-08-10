using System.Text;
using System.Text.Json;
using Condux.Core.Auth;
using Xunit;

namespace Condux.Core.Tests;

// The generic OIDC id_token validator underlying both Google sign-in and enterprise SSO. The shared claim
// checks (aud / exp / email_verified / email / sub) are exercised via GoogleIdTokenTests; here we lock the
// one generalized dimension: the issuer is a parameter, so an org's own IdP issuer is accepted and others
// are not.
public class OidcIdTokenTests
{
    private const string Audience = "condux-client";
    private const string Issuer = "https://idp.acme.example/";
    private static readonly string[] Issuers = [Issuer];
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    [Fact]
    public void Validate_accepts_a_token_from_the_configured_issuer()
    {
        var token = Token(new
        {
            aud = Audience,
            iss = Issuer,
            exp = Now.ToUnixTimeSeconds() + 3600,
            email_verified = true,
            email = "dev@acme.example",
            sub = "idp-sub-1",
        });

        var identity = OidcIdToken.Validate(token, Issuers, Audience, Now);

        Assert.NotNull(identity);
        Assert.Equal("idp-sub-1", identity!.Subject);
        Assert.Equal("dev@acme.example", identity.Email);
    }

    [Fact]
    public void Validate_rejects_a_token_from_a_different_issuer()
    {
        var token = Token(new
        {
            aud = Audience,
            iss = "https://evil.example/",
            exp = Now.ToUnixTimeSeconds() + 3600,
            email_verified = true,
            email = "dev@acme.example",
            sub = "idp-sub-1",
        });

        Assert.Null(OidcIdToken.Validate(token, Issuers, Audience, Now));
    }

    private static string Token(object payload)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var body = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        return $"{header}.{body}.signature-not-checked";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
