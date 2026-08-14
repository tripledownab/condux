using System.Net;
using System.Security.Cryptography;
using Condux.Core.SourceControl;
using Condux.GitHub;
using Xunit;

namespace Condux.GitHub.Tests;

public class GitHubInstallationTokensTests
{
    private sealed class StubHandler(string tokenJson) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            // Read now — the caller disposes the request (and its content) as soon as the call returns.
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(tokenJson),
            };
        }
    }

    private static GitHubAppOptions Options() =>
        new("Iv1.client", RSA.Create(2048).ExportRSAPrivateKeyPem(), "whsec") { ApiBaseUrl = "https://gh.test" };

    [Fact]
    public void Is_a_source_host_tokens_implementation()
    {
        // GitHub's token minter is one implementation of the neutral seam (#145); a GitLab client would be
        // another. Guards the interface from being quietly dropped.
        Assert.IsAssignableFrom<ISourceHostTokens>(new GitHubInstallationTokens(new HttpClient(), Options(), () => DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task Mints_then_caches_the_installation_token()
    {
        var now = DateTimeOffset.UnixEpoch.AddYears(56);
        var handler = new StubHandler($$"""{"token":"ghs_abc","expires_at":"{{now.AddHours(1):O}}"}""");
        using var http = new HttpClient(handler);
        var tokens = new GitHubInstallationTokens(http, Options(), () => now);

        var first = await tokens.GetAsync(42);
        var second = await tokens.GetAsync(42);

        Assert.Equal("ghs_abc", first);
        Assert.Equal("ghs_abc", second);
        Assert.Equal(1, handler.Calls); // the second call is served from cache
        Assert.Equal("Bearer", handler.LastRequest?.Headers.Authorization?.Scheme);
        Assert.Contains("/app/installations/42/access_tokens", handler.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Read_only_mint_downscopes_permissions_and_repository()
    {
        var now = DateTimeOffset.UnixEpoch.AddYears(56);
        var handler = new StubHandler($$"""{"token":"ghs_ro","expires_at":"{{now.AddHours(1):O}}"}""");
        using var http = new HttpClient(handler);
        var tokens = new GitHubInstallationTokens(http, Options(), () => now);

        var token = await tokens.GetReadOnlyAsync(42, "acme/app");

        Assert.Equal("ghs_ro", token);
        // The downscope IS the security property: contents+metadata read, one bare repo name. GitHub
        // enforces it at mint, so asserting the body is asserting what the vendor sandbox can ever do.
        using var parsed = System.Text.Json.JsonDocument.Parse(handler.LastBody!);
        var permissions = parsed.RootElement.GetProperty("permissions");
        Assert.Equal("read", permissions.GetProperty("contents").GetString());
        Assert.Equal("read", permissions.GetProperty("metadata").GetString());
        var repos = parsed.RootElement.GetProperty("repositories");
        Assert.Equal(1, repos.GetArrayLength());
        Assert.Equal("app", repos[0].GetString());
    }

    [Fact]
    public async Task Read_only_and_full_tokens_cache_separately()
    {
        var now = DateTimeOffset.UnixEpoch.AddYears(56);
        var handler = new StubHandler($$"""{"token":"ghs_t","expires_at":"{{now.AddHours(1):O}}"}""");
        using var http = new HttpClient(handler);
        var tokens = new GitHubInstallationTokens(http, Options(), () => now);

        await tokens.GetAsync(42);
        await tokens.GetReadOnlyAsync(42, "acme/app");
        await tokens.GetReadOnlyAsync(42, "acme/app");
        await tokens.GetReadOnlyAsync(42, "acme/other");

        // Full + ro(app) + ro(other) each mint once; the repeated ro(app) is cached. A shared cache
        // entry would hand the full-permission token to the read-only path.
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task The_default_seam_implementation_refuses_rather_than_hands_a_full_token()
    {
        // Any ISourceHostTokens impl that does not override GetReadOnlyAsync (the runner's static
        // tokens, a future GitLab impl) must throw, never silently return its write-capable token.
        ISourceHostTokens fallback = new StaticTokens(); // default members resolve via the interface
        await Assert.ThrowsAsync<NotSupportedException>(
            () => fallback.GetReadOnlyAsync(1, "acme/app"));
    }

    private sealed class StaticTokens : ISourceHostTokens
    {
        public Task<string> GetAsync(long installationId, CancellationToken ct = default) =>
            Task.FromResult("full-token");
    }

    [Fact]
    public async Task Re_mints_when_the_cached_token_is_near_expiry()
    {
        var now = DateTimeOffset.UnixEpoch.AddYears(56);
        // Expires in 30s — inside the 1-minute freshness margin, so the next call re-mints rather than cache.
        var handler = new StubHandler($$"""{"token":"ghs_x","expires_at":"{{now.AddSeconds(30):O}}"}""");
        using var http = new HttpClient(handler);
        var tokens = new GitHubInstallationTokens(http, Options(), () => now);

        await tokens.GetAsync(7);
        await tokens.GetAsync(7);

        Assert.Equal(2, handler.Calls);
    }
}
