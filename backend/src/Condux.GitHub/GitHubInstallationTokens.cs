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
/// every call. The full-permission token is used server-side only; in the fix flow it never enters the
/// agent sandbox. The READ-ONLY variant (ADR-0038) downscopes at mint — contents+metadata read, one
/// repository — and is the only token shape allowed to leave our boundary (the Managed Agents mount).
/// </summary>
public sealed class GitHubInstallationTokens(HttpClient http, GitHubAppOptions options, Func<DateTimeOffset> clock)
    : ISourceHostTokens
{
    private readonly ConcurrentDictionary<string, InstallationToken> cache = new();

    /// <summary>A valid installation token for <paramref name="installationId"/>, minting one if needed.</summary>
    public Task<string> GetAsync(long installationId, CancellationToken ct = default) =>
        GetCachedAsync($"{installationId}", installationId, body: null, ct);

    /// <summary>A read-only token scoped to exactly <paramref name="repoFullName"/> — able to clone,
    /// unable to push or reach any other repo. GitHub enforces the downscope at mint (verified live:
    /// an in-sandbox push with this token is refused with 403).</summary>
    public Task<string> GetReadOnlyAsync(long installationId, string repoFullName, CancellationToken ct = default)
    {
        // The repositories filter takes bare repo names; the installation pins the owner.
        var name = repoFullName.Contains('/') ? repoFullName[(repoFullName.IndexOf('/') + 1)..] : repoFullName;
        var body = new MintRequest(
            new Permissions("read", "read"), [name]);
        return GetCachedAsync($"{installationId}:ro:{repoFullName}", installationId, body, ct);
    }

    private async Task<string> GetCachedAsync(
        string cacheKey, long installationId, MintRequest? body, CancellationToken ct)
    {
        var now = clock();
        if (cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now.AddMinutes(1))
        {
            return cached.Token;
        }

        var jwt = GitHubAppJwt.Create(options, now);
        using var req = new HttpRequestMessage(
            HttpMethod.Post, $"{options.ApiBaseUrl}/app/installations/{installationId}/access_tokens");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Condux", "1.0"));
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }

        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var parsed = await resp.Content.ReadFromJsonAsync<TokenResponse>(ct)
            ?? throw new InvalidOperationException("GitHub returned an empty installation-token response.");

        var token = new InstallationToken(parsed.Token, parsed.ExpiresAt);
        cache[cacheKey] = token;
        return token.Token;
    }

    private sealed record MintRequest(
        [property: JsonPropertyName("permissions")] Permissions Permissions,
        [property: JsonPropertyName("repositories")] IReadOnlyList<string> Repositories);

    private sealed record Permissions(
        [property: JsonPropertyName("contents")] string Contents,
        [property: JsonPropertyName("metadata")] string Metadata);

    private sealed record TokenResponse(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
}
