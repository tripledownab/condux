using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Condux.Core.Tickets;
using Xunit;

namespace Condux.Linear.Tests;

public class LinearApiTests
{
    private static readonly LinearOptions Options =
        new("client-id", "client-secret", "https://app.test/cb") { ApiBaseUrl = "https://api.linear.test" };

    private static readonly TicketContent Content = new("TypeError: x", "**Level:** Error\n");
    private static readonly Guid Id = Guid.Parse("0b9f8a2e-5c3d-4e7f-9a1b-2c3d4e5f6a7b");

    private static LinearApi Api(ScriptedHandler handler) => new(new HttpClient(handler), Options);

    private static string Error(string type) => $$$"""{"errors":[{"message":"no","extensions":{"type":"{{{type}}}"}}]}""";

    // What Linear answered for an issue id it does not hold, measured against a real workspace on 2026-10-21,
    // less the fields nothing here reads.
    private const string NoSuchIssue =
        """{"errors":[{"message":"Entity not found: Issue","path":["issue"],"extensions":{"type":"invalid input","code":"INPUT_ERROR","statusCode":400,"userError":true}}],"data":null}""";

    [Fact]
    public async Task Creates_the_issue_under_our_own_id_and_returns_its_identifier_and_url()
    {
        var handler = new ScriptedHandler().On("/graphql", HttpStatusCode.OK,
            """{"data":{"issueCreate":{"success":true,"issue":{"identifier":"ENG-12","url":"https://linear.test/x/issue/ENG-12"}}}}""");

        var outcome = await Api(handler).CreateIssueAsync("at", "team-1", [], Id, Content);

        Assert.Equal(TicketOutcome.Success(new TicketRef("ENG-12", "https://linear.test/x/issue/ENG-12")), outcome);
        var sent = handler.Sent.Single();
        Assert.Equal("at", sent.Auth);
        var input = JsonDocument.Parse(sent.Body).RootElement.GetProperty("variables").GetProperty("input");
        Assert.Equal(Id.ToString(), input.GetProperty("id").GetString());
        Assert.Equal("team-1", input.GetProperty("teamId").GetString());
        Assert.Equal(Content.Title, input.GetProperty("title").GetString());
        Assert.Equal(Content.Body, input.GetProperty("description").GetString());
    }

    // A project with no labels sends exactly the request it sent before labels existed; one with labels
    // sends their ids.
    [Fact]
    public async Task Sends_label_ids_only_when_the_project_has_some()
    {
        const string Created = """{"data":{"issueCreate":{"success":true,"issue":{"identifier":"ENG-1","url":"https://linear.test/i/ENG-1"}}}}""";
        var handler = new ScriptedHandler().On("/graphql", HttpStatusCode.OK, Created).On("/graphql", HttpStatusCode.OK, Created);

        await Api(handler).CreateIssueAsync("at", "team-1", [], Id, Content);
        await Api(handler).CreateIssueAsync("at", "team-1", ["l1", "l2"], Id, Content);

        var bare = JsonDocument.Parse(handler.Sent[0].Body).RootElement.GetProperty("variables").GetProperty("input");
        Assert.False(bare.TryGetProperty("labelIds", out _));
        var labelled = JsonDocument.Parse(handler.Sent[1].Body).RootElement.GetProperty("variables").GetProperty("input");
        Assert.Equal(["l1", "l2"], labelled.GetProperty("labelIds").EnumerateArray().Select(e => e.GetString()));
    }

    // Workspace labels and the team's own, never group labels, which an issue cannot carry directly.
    [Fact]
    public async Task Lists_every_page_of_the_labels_a_teams_issues_can_carry()
    {
        var handler = new ScriptedHandler()
            .On("/graphql", HttpStatusCode.OK,
                """{"data":{"issueLabels":{"nodes":[{"id":"l1","name":"Bug","color":"#f00"}],"pageInfo":{"hasNextPage":true,"endCursor":"c1"}}}}""")
            .On("/graphql", HttpStatusCode.OK,
                """{"data":{"issueLabels":{"nodes":[{"id":"l2","name":"Backend","color":"#0f0","parent":{"id":"g1","name":"Area"}}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}""");

        var list = await Api(handler).ListLabelsAsync("at", "team-1");

        Assert.True(list.Complete);
        Assert.Equal([new LinearLabel("l1", "Bug", null, null), new LinearLabel("l2", "Backend", "g1", "Area")], list.Labels);
        var first = JsonDocument.Parse(handler.Sent[0].Body).RootElement;
        Assert.Contains("isGroup: { eq: false }", first.GetProperty("query").GetString());
        Assert.Equal("team-1", first.GetProperty("variables").GetProperty("team").GetString());
        Assert.Equal("c1", JsonDocument.Parse(handler.Sent[1].Body).RootElement.GetProperty("variables").GetProperty("after").GetString());
    }

    [Fact]
    public async Task A_label_on_two_pages_is_listed_once()
    {
        var handler = new ScriptedHandler()
            .On("/graphql", HttpStatusCode.OK,
                """{"data":{"issueLabels":{"nodes":[{"id":"l1","name":"Bug"}],"pageInfo":{"hasNextPage":true,"endCursor":"c1"}}}}""")
            .On("/graphql", HttpStatusCode.OK,
                """{"data":{"issueLabels":{"nodes":[{"id":"l1","name":"Bug"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}""");

        var list = await Api(handler).ListLabelsAsync("at", "team-1");

        Assert.Equal([new LinearLabel("l1", "Bug", null, null)], list.Labels);
    }

    // A refused request is a failure only when Linear's answer says it handled the request and said no.
    // Everything else might have created the issue, and calling that a failure invites a duplicate.
    [Theory]
    [InlineData(HttpStatusCode.OK, "forbidden", TicketOutcomeKind.Failed, TicketFailure.TeamUnavailable)]
    [InlineData(HttpStatusCode.BadRequest, "invalid input", TicketOutcomeKind.Failed, TicketFailure.Refused)]
    [InlineData(HttpStatusCode.OK, "usage limit exceeded", TicketOutcomeKind.Failed, TicketFailure.Refused)]
    [InlineData(HttpStatusCode.OK, "ratelimited", TicketOutcomeKind.RetryLater, null)]
    [InlineData(HttpStatusCode.OK, "internal error", TicketOutcomeKind.Uncertain, null)]
    [InlineData(HttpStatusCode.OK, "lock timeout", TicketOutcomeKind.Uncertain, null)]
    [InlineData(HttpStatusCode.OK, "something new", TicketOutcomeKind.Uncertain, null)]
    public async Task Classifies_an_error_by_its_extensions_type(
        HttpStatusCode status, string type, TicketOutcomeKind kind, TicketFailure? failure)
    {
        // A refusal is followed by a lookup under the same id; here Linear says there is no such issue.
        var handler = new ScriptedHandler().On("/graphql", status, Error(type))
            .On("/graphql", HttpStatusCode.OK, NoSuchIssue);

        var outcome = await Api(handler).CreateIssueAsync("at", "team-1", [], Id, Content);

        Assert.Equal(new TicketOutcome(kind, Failure: failure), outcome);
    }

    // Measured against a real workspace: a create that repeats an id Linear already holds is refused. The
    // issue an earlier attempt made is the one we wanted, so finding it under our id records it as created.
    [Fact]
    public async Task A_refused_create_whose_id_already_exists_is_recorded_as_the_issue_an_earlier_attempt_made()
    {
        var handler = new ScriptedHandler()
            .On("/graphql", HttpStatusCode.OK, Error("invalid input"))
            .On("/graphql", HttpStatusCode.OK,
                """{"data":{"issue":{"identifier":"ENG-160","url":"https://linear.test/i/ENG-160"}}}""");

        var outcome = await Api(handler).CreateIssueAsync("at", "team-1", [], Id, Content);

        Assert.Equal(TicketOutcome.Success(new TicketRef("ENG-160", "https://linear.test/i/ENG-160")), outcome);
        var lookup = JsonDocument.Parse(handler.Sent[1].Body).RootElement;
        Assert.Contains("issue(id:", lookup.GetProperty("query").GetString());
        Assert.Equal(Id.ToString(), lookup.GetProperty("variables").GetProperty("id").GetString());
    }

    // The lookup failing leaves it open whether an issue exists, which is not a refusal.
    [Fact]
    public async Task A_refused_create_whose_lookup_fails_is_uncertain()
    {
        var handler = new ScriptedHandler()
            .On("/graphql", HttpStatusCode.OK, Error("invalid input"))
            .Throw("/graphql", new HttpRequestException("down"));

        Assert.Equal(TicketOutcome.Uncertain, await Api(handler).CreateIssueAsync("at", "team-1", [], Id, Content));
    }

    // Any lookup answer that is neither the issue nor a "no such issue" leaves it open whether one exists.
    [Theory]
    [InlineData(HttpStatusCode.OK, """{"errors":[{"message":"no","extensions":{"type":"internal error"}}]}""")]
    [InlineData(HttpStatusCode.OK, """{"data":{"issue":null}}""")]
    [InlineData(HttpStatusCode.InternalServerError, "oops")]
    public async Task A_refused_create_whose_lookup_answers_nothing_usable_is_uncertain(HttpStatusCode status, string lookup)
    {
        var handler = new ScriptedHandler()
            .On("/graphql", HttpStatusCode.OK, Error("invalid input"))
            .On("/graphql", status, lookup);

        Assert.Equal(TicketOutcome.Uncertain, await Api(handler).CreateIssueAsync("at", "team-1", [], Id, Content));
    }

    // A team Linear refused is not a create that may already exist, so no lookup is spent on it.
    [Fact]
    public async Task A_team_refusal_is_not_looked_up()
    {
        var handler = new ScriptedHandler().On("/graphql", HttpStatusCode.OK, Error("forbidden"));

        await Api(handler).CreateIssueAsync("at", "team-1", [], Id, Content);

        Assert.Equal(1, handler.Count("/graphql"));
    }

    // Linear's SDK falls back to the status only when the body carried no typed error.
    [Theory]
    [InlineData(HttpStatusCode.Forbidden, TicketOutcomeKind.Failed)]
    [InlineData(HttpStatusCode.TooManyRequests, TicketOutcomeKind.RetryLater)]
    [InlineData(HttpStatusCode.InternalServerError, TicketOutcomeKind.Uncertain)]
    [InlineData(HttpStatusCode.BadGateway, TicketOutcomeKind.Uncertain)]
    public async Task Falls_back_to_the_status_when_the_body_says_nothing(HttpStatusCode status, TicketOutcomeKind kind)
    {
        var handler = new ScriptedHandler().On("/graphql", status, "<html>oops</html>");

        Assert.Equal(kind, (await Api(handler).CreateIssueAsync("at", "team-1", [], Id, Content)).Kind);
    }

    [Fact]
    public async Task A_200_that_does_not_report_success_is_uncertain_not_created()
    {
        var handler = new ScriptedHandler()
            .On("/graphql", HttpStatusCode.OK, """{"data":{"issueCreate":{"success":false,"issue":null}}}""")
            .On("/graphql", HttpStatusCode.OK, "not json");

        Assert.Equal(TicketOutcome.Uncertain, await Api(handler).CreateIssueAsync("at", "t", [], Id, Content));
        Assert.Equal(TicketOutcome.Uncertain, await Api(handler).CreateIssueAsync("at", "t", [], Id, Content));
    }

    // The URL is rendered as a link, so only an absolute http(s) one counts as a created ticket, and an
    // answer missing a field is not a crash but an attempt we cannot vouch for.
    [Theory]
    [InlineData("""{"identifier":"ENG-1","url":"javascript:alert(1)"}""")]
    [InlineData("""{"identifier":"ENG-1","url":"/relative"}""")]
    [InlineData("""{"identifier":"ENG-1"}""")]
    [InlineData("""{"url":"https://linear.test/i/ENG-1"}""")]
    public async Task A_created_issue_with_an_unusable_identifier_or_url_is_uncertain(string issue)
    {
        var handler = new ScriptedHandler().On("/graphql", HttpStatusCode.OK,
            """{"data":{"issueCreate":{"success":true,"issue":""" + issue + "}}}");

        Assert.Equal(TicketOutcome.Uncertain, await Api(handler).CreateIssueAsync("at", "t", [], Id, Content));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_refused_token_is_thrown_so_the_caller_can_refresh(HttpStatusCode status)
    {
        var handler = new ScriptedHandler().On("/graphql", status, Error("authentication error"));

        await Assert.ThrowsAsync<LinearUnauthorizedException>(() => Api(handler).CreateIssueAsync("at", "t", [], Id, Content));
    }

    [Fact]
    public async Task A_connection_that_never_opened_is_retried_and_one_that_dropped_is_uncertain()
    {
        var handler = new ScriptedHandler()
            .Throw("/graphql", new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException()))
            .Throw("/graphql", new HttpRequestException(HttpRequestError.ResponseEnded, "reset"))
            .Throw("/graphql", new TaskCanceledException("timed out"));

        Assert.Equal(TicketOutcome.RetryLater, await Api(handler).CreateIssueAsync("at", "t", [], Id, Content));
        Assert.Equal(TicketOutcome.Uncertain, await Api(handler).CreateIssueAsync("at", "t", [], Id, Content));
        Assert.Equal(TicketOutcome.Uncertain, await Api(handler).CreateIssueAsync("at", "t", [], Id, Content));
    }

    [Fact]
    public async Task Lists_every_page_of_teams()
    {
        var handler = new ScriptedHandler()
            .On("/graphql", HttpStatusCode.OK,
                """{"data":{"teams":{"nodes":[{"id":"t1","key":"ENG","name":"Engineering"}],"pageInfo":{"hasNextPage":true,"endCursor":"c1"}}}}""")
            .On("/graphql", HttpStatusCode.OK,
                """{"data":{"teams":{"nodes":[{"id":"t2","key":"OPS","name":"Ops"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}""");

        var teams = await Api(handler).ListTeamsAsync("at");

        Assert.Equal([new LinearTeam("t1", "ENG", "Engineering"), new LinearTeam("t2", "OPS", "Ops")], teams);
        var second = JsonDocument.Parse(handler.Sent[1].Body).RootElement.GetProperty("variables");
        Assert.Equal("c1", second.GetProperty("after").GetString());
    }

    // An answer missing a field, or carrying a null where a value belongs, is one the callers already treat
    // as a failed call; it must not escape as a KeyNotFoundException and a 500.
    [Theory]
    [InlineData("""{"data":{"organization":{"id":null,"name":"Acme"}}}""")]
    [InlineData("""{"data":{"organization":{"name":"Acme"}}}""")]
    [InlineData("""{"data":{"organization":null}}""")]
    public async Task A_misshapen_workspace_answer_is_a_failed_call(string body)
    {
        var handler = new ScriptedHandler().On("/graphql", HttpStatusCode.OK, body);

        await Assert.ThrowsAsync<HttpRequestException>(() => Api(handler).GetWorkspaceAsync("at"));
    }

    [Fact]
    public async Task A_misshapen_teams_answer_is_a_failed_call()
    {
        var handler = new ScriptedHandler().On("/graphql", HttpStatusCode.OK,
            """{"data":{"teams":{"nodes":[{"id":"t1","key":null,"name":"Eng"}],"pageInfo":{"hasNextPage":false}}}}""");

        await Assert.ThrowsAsync<HttpRequestException>(() => Api(handler).ListTeamsAsync("at"));
    }

    [Fact]
    public async Task Reads_the_workspace_and_throws_on_anything_that_is_not_one()
    {
        var handler = new ScriptedHandler()
            .On("/graphql", HttpStatusCode.OK, """{"data":{"organization":{"id":"w1","name":"Acme"}}}""")
            .On("/graphql", HttpStatusCode.OK, Error("forbidden"))
            .On("/graphql", HttpStatusCode.Unauthorized, "{}");

        Assert.Equal(new LinearWorkspace("w1", "Acme"), await Api(handler).GetWorkspaceAsync("at"));
        await Assert.ThrowsAsync<HttpRequestException>(() => Api(handler).GetWorkspaceAsync("at"));
        await Assert.ThrowsAsync<LinearUnauthorizedException>(() => Api(handler).GetWorkspaceAsync("at"));
    }
}
