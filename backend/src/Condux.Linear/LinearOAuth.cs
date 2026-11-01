using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Condux.Linear;

/// <summary>The Linear OAuth application this deployment registered, from <c>CONDUX_LINEAR_*</c>.</summary>
public sealed record LinearOptions(string ClientId, string ClientSecret, string RedirectUri)
{
    /// <summary>Where the browser authorizes. Overridable for tests.</summary>
    public string AuthorizeEndpoint { get; init; } = "https://linear.app/oauth/authorize";

    /// <summary>The token, revoke and GraphQL host. Overridable for tests.</summary>
    public string ApiBaseUrl { get; init; } = "https://api.linear.app";
}

/// <summary>A grant as Linear issues it. Both tokens are replaced on every refresh.</summary>
public sealed record LinearTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>What a refresh established: new tokens, or a refusal (the org has to reconnect), or neither,
/// which means Linear could not be asked and the grant is not known to be bad.</summary>
public sealed record LinearRefresh(LinearTokens? Tokens, bool Refused);

/// <summary>
/// Linear's OAuth 2.0 endpoints, as Linear's OAuth 2.0 guide documents them (read 2026-10-20, cited in
/// ADR-0053): authorize in the browser, exchange the code, refresh and revoke. The grant acts as the app
/// (<c>actor=app</c>) with the two narrow scopes ADR-0053 chose, so a ticket is created by Condux rather
/// than by whoever connected, and the token cannot do more than read and create issues.
/// </summary>
public sealed class LinearOAuth(HttpClient http, LinearOptions options, Func<DateTimeOffset> clock)
{
    public const string Scopes = "read,issues:create";

    public string BuildAuthorizeUrl(string state) =>
        $"{options.AuthorizeEndpoint}"
        + $"?client_id={Uri.EscapeDataString(options.ClientId)}"
        + $"&redirect_uri={Uri.EscapeDataString(options.RedirectUri)}"
        + "&response_type=code"
        + $"&scope={Uri.EscapeDataString(Scopes)}"
        + "&actor=app"
        + $"&state={Uri.EscapeDataString(state)}";

    /// <summary>Exchange the authorization code for a grant, or null if Linear refuses it.</summary>
    public async Task<LinearTokens?> ExchangeCodeAsync(string code, CancellationToken ct = default)
    {
        using var response = await PostFormAsync("/oauth/token", new()
        {
            ["code"] = code,
            ["redirect_uri"] = options.RedirectUri,
            ["client_id"] = options.ClientId,
            ["client_secret"] = options.ClientSecret,
            ["grant_type"] = "authorization_code",
        }, ct);
        return response.IsSuccessStatusCode ? await ReadTokensAsync(response, ct) : null;
    }

    /// <summary>
    /// Renew the grant. A 400 or 401 is Linear refusing this refresh token, which only reconnecting fixes.
    /// Anything else, including not reaching Linear, says nothing about the grant, so the caller keeps it.
    /// </summary>
    public async Task<LinearRefresh> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await PostFormAsync("/oauth/token", new()
            {
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
            }, ct);
        }
        catch (Exception e) when (LinearHttp.IsCallFailure(e, ct))
        {
            return new LinearRefresh(null, Refused: false);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                return new LinearRefresh(null, Refused: true);
            }
            return new LinearRefresh(
                response.IsSuccessStatusCode ? await ReadTokensAsync(response, ct) : null, Refused: false);
        }
    }

    /// <summary>Revoke a token, true when Linear confirms it. Linear also answers 400 for a token it has
    /// already revoked, and a 400 cannot tell that from any other refusal, so it reads as not confirmed and
    /// the caller tells the person to check in Linear.</summary>
    public async Task<bool> RevokeAsync(string token, string tokenTypeHint, CancellationToken ct = default)
    {
        try
        {
            using var response = await PostFormAsync("/oauth/revoke", new()
            {
                ["token"] = token,
                ["token_type_hint"] = tokenTypeHint,
            }, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (LinearHttp.IsCallFailure(e, ct))
        {
            // Not reaching Linear, or a timeout, leaves the token unrevoked, which the caller reports.
            return false;
        }
    }

    private Task<HttpResponseMessage> PostFormAsync(string path, Dictionary<string, string> form, CancellationToken ct) =>
        http.PostAsync($"{options.ApiBaseUrl}{path}", new FormUrlEncodedContent(form), ct);

    private async Task<LinearTokens?> ReadTokensAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = JsonSerializer.Deserialize<TokenResponse>(await response.Content.ReadAsStringAsync(ct));
            return body is { AccessToken: { Length: > 0 } access, RefreshToken: { Length: > 0 } refresh, ExpiresIn: > 0 }
                ? new LinearTokens(access, refresh, clock().AddSeconds(body.ExpiresIn))
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("expires_in")] long ExpiresIn);
}
