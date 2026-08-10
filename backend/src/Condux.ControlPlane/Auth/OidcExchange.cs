using System.Text.Json;

namespace Condux.ControlPlane.Auth;

/// <summary>
/// The OIDC authorization-code → id_token exchange, shared by Google sign-in (#71) and per-org enterprise
/// SSO (#72). One server-to-server POST to the provider's token endpoint over TLS (authenticated with the
/// client id + secret), returning the raw id_token string. The claims are validated separately by the pure
/// <see cref="Condux.Core.Auth.OidcIdToken"/>; the access token is never kept. Returns null on any failure
/// — a non-success response, a malformed body, or a missing id_token.
/// </summary>
internal static class OidcExchange
{
    public static async Task<string?> FetchIdTokenAsync(
        HttpClient http, string tokenEndpoint, string clientId, string clientSecret,
        string redirectUri, string code, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        });

        using var response = await http.PostAsync(tokenEndpoint, form, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("id_token", out var t) ? t.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
