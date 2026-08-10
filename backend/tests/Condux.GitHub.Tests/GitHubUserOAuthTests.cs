using System.Net;
using System.Text;
using Xunit;

namespace Condux.GitHub.Tests;

/// <summary>
/// The user-authorization leg, against a stub GitHub. Covers what the connect flow depends on: the browser
/// URL, the code exchange (including GitHub's habit of reporting failure with a 200), and the installations
/// read that decides whether we link, install, or ask.
/// </summary>
public class GitHubUserOAuthTests
{
    private const string ClientId = "Iv1.test";

    private static GitHubUserOAuth Create(StubHandler handler) =>
        new(new HttpClient(handler))
        {
            OAuthBaseUrl = "https://github.test",
            ApiBaseUrl = "https://api.github.test",
        };

    [Fact]
    public void Authorize_url_carries_the_client_id_state_and_callback()
    {
        var url = Create(new StubHandler()).AuthorizeUrl(
            ClientId, "https://app.condux.test/api/github/oauth/callback", "state+token");

        Assert.StartsWith("https://github.test/login/oauth/authorize?", url);
        Assert.Contains($"client_id={ClientId}", url);
        Assert.Contains("state=state%2Btoken", url);
        Assert.Contains(
            "redirect_uri=https%3A%2F%2Fapp.condux.test%2Fapi%2Fgithub%2Foauth%2Fcallback", url);
    }

    [Fact]
    public async Task Exchanges_a_code_for_a_user_token()
    {
        var handler = new StubHandler { Body = """{"access_token":"ghu_abc","token_type":"bearer"}""" };
        var token = await Create(handler).ExchangeCodeAsync(
            ClientId, "secret", "https://app.condux.test/cb", "the-code");

        Assert.Equal("ghu_abc", token);
        Assert.Equal("https://github.test/login/oauth/access_token", handler.RequestedUrl);
        // Without an explicit JSON Accept, GitHub answers form-encoded and the parse would fail.
        Assert.Contains("application/json", handler.AcceptHeader);
        Assert.Contains("code=the-code", handler.RequestBody);
        Assert.Contains("client_secret=secret", handler.RequestBody);
    }

    // A used or expired code still comes back 200, with an error member instead of a token.
    [Fact]
    public async Task Returns_null_when_github_reports_a_bad_code_or_fails()
    {
        var badCode = Create(new StubHandler { Body = """{"error":"bad_verification_code"}""" });
        Assert.Null(await badCode.ExchangeCodeAsync(ClientId, "secret", "https://app.condux.test/cb", "x"));

        var failed = Create(new StubHandler { Status = HttpStatusCode.BadRequest, Body = "nope" });
        Assert.Null(await failed.ExchangeCodeAsync(ClientId, "secret", "https://app.condux.test/cb", "x"));

        var garbage = Create(new StubHandler { Body = "not json" });
        Assert.Null(await garbage.ExchangeCodeAsync(ClientId, "secret", "https://app.condux.test/cb", "x"));
    }

    [Fact]
    public async Task Lists_the_installations_the_user_can_reach_with_their_own_token()
    {
        var handler = new StubHandler
        {
            Body = """
                {"total_count":2,"installations":[
                  {"id":7,"app_slug":"condux","account":{"login":"acme"}},
                  {"id":9,"app_slug":"condux","account":{"login":"acme-labs"}}]}
                """,
        };

        var installations = await Create(handler).ListInstallationsAsync("ghu_abc", "condux");

        Assert.Equal("https://api.github.test/user/installations", handler.RequestedUrl);
        // Asked as the user, never with the app JWT — the app-wide list would be every customer.
        Assert.Equal("Bearer ghu_abc", handler.AuthorizationHeader);
        Assert.Equal([7, 9], installations.Select(i => i.InstallationId));
        Assert.Equal(["acme", "acme-labs"], installations.Select(i => i.AccountLogin));
    }

    [Fact]
    public async Task Ignores_installations_belonging_to_another_app()
    {
        var handler = new StubHandler
        {
            Body = """
                {"total_count":2,"installations":[
                  {"id":7,"app_slug":"condux","account":{"login":"acme"}},
                  {"id":8,"app_slug":"something-else","account":{"login":"acme"}}]}
                """,
        };

        var installations = await Create(handler).ListInstallationsAsync("ghu_abc", "condux");
        Assert.Equal([7], installations.Select(i => i.InstallationId));
    }

    [Fact]
    public async Task Reports_no_installations_when_the_user_authorized_without_installing()
    {
        var handler = new StubHandler { Body = """{"total_count":0,"installations":[]}""" };
        Assert.Empty(await Create(handler).ListInstallationsAsync("ghu_abc", "condux"));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public string Body { get; init; } = "{}";
        public string? RequestedUrl { get; private set; }
        public string RequestBody { get; private set; } = "";
        public string? AuthorizationHeader { get; private set; }
        public string AcceptHeader { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrl = request.RequestUri!.ToString();
            AuthorizationHeader = request.Headers.Authorization?.ToString();
            AcceptHeader = string.Join(",", request.Headers.Accept.Select(a => a.MediaType));
            if (request.Content is not null)
            {
                RequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
