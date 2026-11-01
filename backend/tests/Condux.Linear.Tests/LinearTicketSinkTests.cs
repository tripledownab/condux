using System.Net;
using System.Web;
using Condux.Core.Secrets;
using Condux.Core.Tickets;
using Xunit;

namespace Condux.Linear.Tests;

public class LinearTicketSinkTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly SecretBox Secrets = new(Convert.ToBase64String(new byte[32]));
    private static readonly TicketContent Content = new("TypeError: x", "body\n");

    private const string Created =
        """{"data":{"issueCreate":{"success":true,"issue":{"identifier":"ENG-1","url":"https://linear.test/i/ENG-1"}}}}""";
    private const string Unauthorized = """{"errors":[{"message":"no","extensions":{"type":"authentication error"}}]}""";

    private static readonly LinearOptions Options =
        new("id", "secret", "https://app.test/cb") { ApiBaseUrl = "https://api.linear.test" };

    private static TrackerConnection Connection(string access, string refresh, DateTimeOffset expires, DateTimeOffset? broken = null) =>
        new(7, TicketProvider.Linear, "w1", "Acme", Secrets.Seal(access), Secrets.Seal(refresh), expires, broken);

    private static (LinearTicketSink Sink, ScriptedHandler Http, MemoryConnectionStore Store) Build(TrackerConnection? row)
    {
        var http = new ScriptedHandler();
        var store = new MemoryConnectionStore { Row = row };
        var client = new HttpClient(http);
        var sink = new LinearTicketSink(
            store, Secrets, new LinearOAuth(client, Options, () => Now), new LinearApi(client, Options), () => Now);
        return (sink, http, store);
    }

    private static string? RefreshTokenSent(ScriptedHandler http) =>
        HttpUtility.ParseQueryString(http.Sent.First(s => s.Path == "/oauth/token").Body)["refresh_token"];

    [Fact]
    public async Task Uses_a_live_access_token_without_refreshing()
    {
        var (sink, http, _) = Build(Connection("at", "rt", Now.AddHours(1)));
        http.On("/graphql", HttpStatusCode.OK, Created);

        var outcome = await sink.CreateAsync(7, "team-1", [], Guid.NewGuid(), Content);

        Assert.Equal(TicketOutcomeKind.Created, outcome.Kind);
        Assert.Equal(0, http.Count("/oauth/token"));
        Assert.Equal("at", http.Sent.Single().Auth);
    }

    [Fact]
    public async Task Refreshes_an_expired_token_and_stores_the_replacement_pair()
    {
        var (sink, http, store) = Build(Connection("at", "rt", Now.AddMinutes(1)));
        http.On("/oauth/token", HttpStatusCode.OK, """{"access_token":"at2","refresh_token":"rt2","expires_in":86399}""")
            .On("/graphql", HttpStatusCode.OK, Created);

        await sink.CreateAsync(7, "team-1", [], Guid.NewGuid(), Content);

        Assert.Equal("rt", RefreshTokenSent(http));
        Assert.Equal("at2", http.Sent.Last().Auth);
        Assert.Equal("at2", Secrets.Open(store.Row!.AccessTokenSealed));
        Assert.Equal("rt2", Secrets.Open(store.Row.RefreshTokenSealed));
        Assert.Equal(Now.AddSeconds(86399), store.Row.AccessExpiresAt);
    }

    [Fact]
    public async Task Losing_the_refresh_race_uses_the_winners_token_rather_than_its_own()
    {
        var (sink, http, store) = Build(Connection("at", "rt", Now.AddMinutes(1)));
        store.BeforeWrite = () => store.Row = Connection("winner-at", "winner-rt", Now.AddHours(24));
        http.On("/oauth/token", HttpStatusCode.OK, """{"access_token":"mine","refresh_token":"mine-rt","expires_in":86399}""")
            .On("/graphql", HttpStatusCode.OK, Created);

        await sink.CreateAsync(7, "team-1", [], Guid.NewGuid(), Content);

        Assert.Equal("winner-at", http.Sent.Last().Auth);
        Assert.Equal("winner-rt", Secrets.Open(store.Row!.RefreshTokenSealed));
    }

    [Fact]
    public async Task A_refused_refresh_marks_the_connection_broken_and_sends_nothing()
    {
        var (sink, http, store) = Build(Connection("at", "rt", Now.AddMinutes(1)));
        http.On("/oauth/token", HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");

        var outcome = await sink.CreateAsync(7, "team-1", [], Guid.NewGuid(), Content);

        Assert.Equal(TicketOutcome.Fail(TicketFailure.ConnectionBroken), outcome);
        Assert.NotNull(store.Row!.BrokenAt);
        Assert.Equal(0, http.Count("/graphql"));
    }

    // The refused token was already replaced by another process, whose renewal must stand.
    [Fact]
    public async Task A_stale_refusal_does_not_break_a_connection_another_process_renewed()
    {
        var (sink, http, store) = Build(Connection("at", "rt", Now.AddMinutes(1)));
        store.BeforeWrite = () => store.Row = Connection("winner-at", "winner-rt", Now.AddHours(24));
        http.On("/oauth/token", HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")
            .On("/graphql", HttpStatusCode.OK, Created);

        var outcome = await sink.CreateAsync(7, "team-1", [], Guid.NewGuid(), Content);

        Assert.Equal(TicketOutcomeKind.Created, outcome.Kind);
        Assert.Null(store.Row!.BrokenAt);
        Assert.Equal("winner-at", http.Sent.Last().Auth);
    }

    [Fact]
    public async Task An_unreachable_token_endpoint_retries_later_and_keeps_the_grant()
    {
        var (sink, http, store) = Build(Connection("at", "rt", Now.AddMinutes(1)));
        http.On("/oauth/token", HttpStatusCode.InternalServerError, "{}");

        Assert.Equal(TicketOutcome.RetryLater, await sink.CreateAsync(7, "team-1", [], Guid.NewGuid(), Content));
        Assert.Null(store.Row!.BrokenAt);
        Assert.Equal("rt", Secrets.Open(store.Row.RefreshTokenSealed));
    }

    [Fact]
    public async Task A_token_refused_before_its_expiry_is_refreshed_once_and_the_same_issue_asked_for_again()
    {
        var id = Guid.NewGuid();
        var (sink, http, _) = Build(Connection("at", "rt", Now.AddHours(1)));
        http.On("/graphql", HttpStatusCode.OK, Unauthorized)
            .On("/oauth/token", HttpStatusCode.OK, """{"access_token":"at2","refresh_token":"rt2","expires_in":86399}""")
            .On("/graphql", HttpStatusCode.OK, Created);

        Assert.Equal(TicketOutcomeKind.Created, (await sink.CreateAsync(7, "team-1", [], id, Content)).Kind);

        var creates = http.Sent.Where(s => s.Path == "/graphql").ToList();
        Assert.Equal(2, creates.Count);
        Assert.All(creates, c => Assert.Contains(id.ToString(), c.Body));
    }

    [Fact]
    public async Task A_freshly_issued_token_that_is_refused_too_means_the_connection_is_broken()
    {
        var (sink, http, _) = Build(Connection("at", "rt", Now.AddHours(1)));
        http.On("/graphql", HttpStatusCode.OK, Unauthorized)
            .On("/oauth/token", HttpStatusCode.OK, """{"access_token":"at2","refresh_token":"rt2","expires_in":86399}""")
            .On("/graphql", HttpStatusCode.OK, Unauthorized);

        Assert.Equal(TicketOutcome.Fail(TicketFailure.ConnectionBroken), await sink.CreateAsync(7, "t", [], Guid.NewGuid(), Content));
        Assert.Equal(1, http.Count("/oauth/token"));
    }

    [Fact]
    public async Task Sends_nothing_for_an_org_that_is_not_connected_or_whose_connection_is_broken()
    {
        var (unconnected, http1, _) = Build(null);
        Assert.Equal(TicketOutcome.Fail(TicketFailure.NotConnected), await unconnected.CreateAsync(7, "t", [], Guid.NewGuid(), Content));
        Assert.Empty(http1.Sent);

        var (broken, http2, _) = Build(Connection("at", "rt", Now.AddHours(1), broken: Now));
        Assert.Equal(TicketOutcome.Fail(TicketFailure.ConnectionBroken), await broken.CreateAsync(7, "t", [], Guid.NewGuid(), Content));
        Assert.Empty(http2.Sent);
    }
}
