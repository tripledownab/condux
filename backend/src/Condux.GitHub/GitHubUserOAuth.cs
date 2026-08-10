using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Condux.GitHub;

/// <summary>An installation of our GitHub App that the authorizing user can reach.</summary>
public sealed record GithubUserInstallation(long InstallationId, string AccountLogin, string? AppSlug);

/// <summary>
/// The GitHub App <em>user-authorization</em> (OAuth) leg. Distinct from the app JWT and installation
/// tokens: this acts <em>as the user</em>, and its one job is to answer "which installations of this app
/// can you reach?" so a connect can find an install that already exists.
///
/// That question has no server-to-server answer we may use. The app JWT can list every installation of
/// the app — which is every customer — so it must never back a user-facing lookup. Asking with the user's
/// own token returns only what that user already has access to, which is exactly the right scope.
/// </summary>
public sealed class GitHubUserOAuth(HttpClient http)
{
    /// <summary>Where the browser authorizes. Overridable for tests and GitHub Enterprise.</summary>
    public string OAuthBaseUrl { get; init; } = "https://github.com";

    /// <summary>The REST API base. Overridable for tests and GitHub Enterprise.</summary>
    public string ApiBaseUrl { get; init; } = "https://api.github.com";

    /// <summary>
    /// Where to send the browser to authorize. Unlike the install URL this always comes back to us, whether
    /// or not the app is already installed — which is what makes re-connecting possible.
    /// </summary>
    public string AuthorizeUrl(string clientId, string redirectUri, string state) =>
        $"{OAuthBaseUrl}/login/oauth/authorize"
        + $"?client_id={Uri.EscapeDataString(clientId)}"
        + $"&redirect_uri={Uri.EscapeDataString(redirectUri)}"
        + $"&state={Uri.EscapeDataString(state)}";

    /// <summary>
    /// Exchange the authorization code for a user access token (<c>ghu_…</c>), or null if GitHub rejects it.
    /// The token is used for the single installations read below and then dropped; it is never stored.
    /// </summary>
    public async Task<string?> ExchangeCodeAsync(
        string clientId, string clientSecret, string redirectUri, string code, CancellationToken ct = default)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{OAuthBaseUrl}/login/oauth/access_token")
        {
            Content = form,
        };
        // Without this GitHub answers form-encoded.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Condux", "1.0"));

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            // A rejected code still comes back 200, with an "error" member instead of a token.
            return doc.RootElement.TryGetProperty("access_token", out var token) ? token.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The installations of this app the user can reach. GitHub scopes the response to the authenticating
    /// app, and we filter on the slug as well so a shared token could never surface another app's installs.
    /// </summary>
    public async Task<IReadOnlyList<GithubUserInstallation>> ListInstallationsAsync(
        string userToken, string? appSlug, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseUrl}/user/installations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Condux", "1.0"));

        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<InstallationsResponse>(ct);

        return
        [
            .. (body?.Installations ?? [])
                .Where(i => string.IsNullOrEmpty(appSlug) || i.AppSlug is null || i.AppSlug == appSlug)
                .Select(i => new GithubUserInstallation(i.Id, i.Account?.Login ?? "", i.AppSlug)),
        ];
    }

    private sealed record InstallationsResponse(
        [property: JsonPropertyName("installations")] IReadOnlyList<InstallationResponse>? Installations);

    private sealed record InstallationResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("account")] AccountResponse? Account,
        [property: JsonPropertyName("app_slug")] string? AppSlug);

    private sealed record AccountResponse([property: JsonPropertyName("login")] string? Login);
}
