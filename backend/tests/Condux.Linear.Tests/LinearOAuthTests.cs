using System.Net;
using System.Web;
using Xunit;

namespace Condux.Linear.Tests;

public class LinearOAuthTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);

    private static readonly LinearOptions Options =
        new("client-id", "client-secret", "https://app.test/api/linear/oauth/callback")
        {
            AuthorizeEndpoint = "https://linear.test/oauth/authorize",
            ApiBaseUrl = "https://api.linear.test",
        };

    private static LinearOAuth OAuth(ScriptedHandler handler) => new(new HttpClient(handler), Options, () => Now);

    [Fact]
    public void Authorize_asks_for_the_two_narrow_scopes_as_the_app_and_carries_the_state()
    {
        var url = new Uri(OAuth(new ScriptedHandler()).BuildAuthorizeUrl("st/ate"));
        var query = HttpUtility.ParseQueryString(url.Query);

        Assert.Equal("https://linear.test/oauth/authorize", url.GetLeftPart(UriPartial.Path));
        Assert.Equal("read,issues:create", query["scope"]);
        Assert.Equal("app", query["actor"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("st/ate", query["state"]);
        Assert.Equal(Options.RedirectUri, query["redirect_uri"]);
    }

    [Fact]
    public async Task Exchanges_a_code_for_both_tokens_and_dates_the_expiry_from_the_clock()
    {
        var handler = new ScriptedHandler().On("/oauth/token", HttpStatusCode.OK,
            """{"access_token":"at","refresh_token":"rt","expires_in":86399,"token_type":"Bearer"}""");

        var tokens = await OAuth(handler).ExchangeCodeAsync("the-code");

        Assert.Equal(new LinearTokens("at", "rt", Now.AddSeconds(86399)), tokens);
        var form = HttpUtility.ParseQueryString(handler.Sent.Single().Body);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("the-code", form["code"]);
        Assert.Equal("client-secret", form["client_secret"]);
        Assert.Equal(Options.RedirectUri, form["redirect_uri"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")]
    [InlineData(HttpStatusCode.OK, """{"access_token":"at"}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task A_refused_or_incomplete_exchange_yields_nothing(HttpStatusCode status, string body)
    {
        var handler = new ScriptedHandler().On("/oauth/token", status, body);
        Assert.Null(await OAuth(handler).ExchangeCodeAsync("the-code"));
    }

    [Fact]
    public async Task A_refresh_sends_the_refresh_grant_and_returns_the_replacement_pair()
    {
        var handler = new ScriptedHandler().On("/oauth/token", HttpStatusCode.OK,
            """{"access_token":"at2","refresh_token":"rt2","expires_in":100}""");

        var refresh = await OAuth(handler).RefreshAsync("rt1");

        Assert.Equal(new LinearTokens("at2", "rt2", Now.AddSeconds(100)), refresh.Tokens);
        Assert.False(refresh.Refused);
        var form = HttpUtility.ParseQueryString(handler.Sent.Single().Body);
        Assert.Equal("refresh_token", form["grant_type"]);
        Assert.Equal("rt1", form["refresh_token"]);
    }

    // Only Linear saying no to this token means reconnecting. An outage must not cost a working grant.
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    public async Task Only_a_400_or_401_counts_as_a_refused_refresh(HttpStatusCode status, bool refused)
    {
        var handler = new ScriptedHandler().On("/oauth/token", status, "{}");

        var refresh = await OAuth(handler).RefreshAsync("rt1");

        Assert.Null(refresh.Tokens);
        Assert.Equal(refused, refresh.Refused);
    }

    [Fact]
    public async Task Not_reaching_linear_is_not_a_refusal()
    {
        var handler = new ScriptedHandler().Throw("/oauth/token", new HttpRequestException("down"));

        var refresh = await OAuth(handler).RefreshAsync("rt1");

        Assert.Equal(new LinearRefresh(null, Refused: false), refresh);
    }

    [Fact]
    public async Task Revokes_with_the_token_field()
    {
        var handler = new ScriptedHandler().On("/oauth/revoke", HttpStatusCode.OK, "");

        Assert.True(await OAuth(handler).RevokeAsync("rt1", "refresh_token"));
        var form = HttpUtility.ParseQueryString(handler.Sent.Single().Body);
        Assert.Equal("rt1", form["token"]);
        Assert.Equal("refresh_token", form["token_type_hint"]);
    }
}
