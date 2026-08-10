using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Condux.Core.SourceControl;

namespace Condux.GitHub;

/// <summary>An installation access token and when it expires.</summary>
public readonly record struct InstallationToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Exchanges the app JWT for an installation access token, scoped to that installation's repos and
/// permissions (~1h TTL), and caches each token until shortly before it expires so we don't re-mint on
/// every call. The token is used server-side only; in the fix flow it never enters the agent sandbox.
/// </summary>
public sealed class GitHubInstallationTokens(HttpClient http, GitHubAppOptions options, Func<DateTimeOffset> clock)
    : ISourceHostTokens
{
    private readonly ConcurrentDictionary<long, InstallationToken> cache = new();

    /// <summary>A valid installation token for <paramref name="installationId"/>, minting one if needed.</summary>
    public async Task<string> GetAsync(long installationId, CancellationToken ct = default)
    {
        var now = clock();
        if (cache.TryGetValue(installationId, out var cached) && cached.ExpiresAt > now.AddMinutes(1))
        {
            return cached.Token;
        }

        var jwt = GitHubAppJwt.Create(options, now);
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"{options.ApiBaseUrl}/app/installations/{installationId}/access_tokens");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Condux", "1.0"));

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<TokenResponse>(ct)
            ?? throw new InvalidOperationException("GitHub returned an empty installation-token response.");

        var token = new InstallationToken(body.Token, body.ExpiresAt);
        cache[installationId] = token;
        return token.Token;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
}
