using System.Net;
using System.Text;
using System.Text.Json;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// A stubbed OIDC token endpoint and the id_token it hands back, shared by the two flows that exchange
/// an authorization code: "sign in with Google" (#71) and per-org enterprise SSO (#72). They run the
/// same <c>OidcExchange</c> and the same <c>OidcIdToken</c> in production and differ only in the issuer
/// and audience they accept, so those are parameters here rather than a second copy of this file.
///
/// <para>The token carries no real signature. That is not a shortcut: the id_token arrives on a
/// server-to-server response over TLS, so the signature is deliberately not re-verified (OIDC Core
/// 3.1.3.7) and the claims are what the validator reads. A test that signed it would be proving
/// something the product does not do.</para>
/// </summary>
internal static class OidcStub
{
    /// <summary>A token endpoint that answers every exchange with the same id_token.</summary>
    public sealed class TokenEndpoint(string idToken) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"id_token":"{{idToken}}"}""", Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>A token endpoint that fails, so the caller sees an exchange that returned nothing.</summary>
    public sealed class FailingTokenEndpoint : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
    }

    /// <summary>
    /// A valid id_token for the given issuer and audience. <paramref name="emailVerified"/> is a
    /// parameter because false is a case the validator must refuse, not an edge: an address the
    /// provider has not verified is the one thing that makes linking by email unsafe.
    /// </summary>
    public static string IdToken(
        string issuer, string audience, string email, bool emailVerified = true) => Encode(new
        {
            aud = audience,
            iss = issuer,
            exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600,
            email_verified = emailVerified,
            email,
            sub = "oidc-" + email,
        });

    /// <summary>Header and payload, base64url, with the third segment a placeholder.</summary>
    public static string Encode(object payload)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var body = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
        return $"{header}.{body}.signature-not-checked";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
