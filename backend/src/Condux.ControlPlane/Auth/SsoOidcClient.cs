using Condux.Core.Auth;
using Condux.Storage.Postgres;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// Exchanges an enterprise-SSO authorization code for the user's verified identity against the org's own
/// IdP (#72). Same shape as <see cref="GoogleOidcClient"/>, but the token endpoint, issuer and client id
/// come from the org's stored <see cref="StoredSsoConfig"/> (not a per-deployment env singleton) and the
/// caller supplies the decrypted client secret. The token is fetched server-to-server over TLS via the
/// shared <see cref="OidcExchange"/>, so the signature is not re-verified (OIDC Core 3.1.3.7); the pure
/// <see cref="OidcIdToken"/> validates the claims against the org's configured issuer.
/// </summary>
internal sealed class SsoOidcClient(HttpClient http)
{
    public async Task<OidcIdentity?> ExchangeCodeAsync(
        StoredSsoConfig config, string clientSecret, string redirectUri, string code,
        DateTimeOffset now, CancellationToken ct)
    {
        var idToken = await OidcExchange.FetchIdTokenAsync(
            http, config.TokenEndpoint, config.ClientId, clientSecret, redirectUri, code, ct);
        return OidcIdToken.Validate(idToken, [config.Issuer], config.ClientId, now);
    }
}
