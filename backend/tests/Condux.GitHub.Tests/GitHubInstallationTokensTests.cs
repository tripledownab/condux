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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(tokenJson),
            });
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
