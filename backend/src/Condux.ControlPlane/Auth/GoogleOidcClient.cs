using Condux.ControlPlane.Setup;
using Condux.Core.Auth;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// Exchanges a Google authorization code for the user's verified identity via the shared
/// <see cref="OidcExchange"/> (one server-to-server POST over TLS), then validates the returned id_token's
/// claims with the pure <see cref="GoogleIdToken"/>. The access token is never stored — only the resulting
/// <see cref="OidcIdentity"/> is used to sign the user in.
/// </summary>
internal sealed class GoogleOidcClient(HttpClient http, GoogleOAuthConfig config)
{
    // Overridable so tests can point the exchange at a stub instead of the real Google endpoint.
    public string TokenEndpoint { get; init; } = "https://oauth2.googleapis.com/token";

    public async Task<OidcIdentity?> ExchangeCodeAsync(string code, DateTimeOffset now, CancellationToken ct)
    {
        var idToken = await OidcExchange.FetchIdTokenAsync(
            http, TokenEndpoint, config.ClientId!, config.ClientSecret!, config.RedirectUri!, code, ct);
        return GoogleIdToken.Validate(idToken, config.ClientId!, now);
    }
}
