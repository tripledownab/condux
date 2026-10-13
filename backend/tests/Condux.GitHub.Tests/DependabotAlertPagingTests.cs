using System.Net;
using Condux.GitHub;
using Xunit;

namespace Condux.GitHub.Tests;

/// <summary>
/// The Dependabot read walks GitHub's cursor pages by following <c>Link: rel="next"</c>, stops at a
/// ceiling, and says so whenever it stopped before the end. Reading only the first page made a repo with
/// more than 100 open alerts show exactly 100, as if that were the total.
/// </summary>
public class DependabotAlertPagingTests
{
    private const string Token = "ghs_test";
    private const string Repo = "acme/api";
    private const string BaseUrl = "https://gh.test";
    private const string FirstPage = "/repos/acme/api/dependabot/alerts?state=open&per_page=100";

    /// <summary>Answers each path with its alerts and, when given, a Link header, or with a failure status;
    /// a path listed in <c>throwing</c> fails as the network would. Records every request.</summary>
    private sealed class PagingHandler(
        Dictionary<string, (string Body, string? Link)> pages,
        Dictionary<string, HttpStatusCode>? failing = null,
        HashSet<string>? throwing = null,
        HashSet<string>? timingOut = null) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var path = request.RequestUri!.PathAndQuery;
            if (throwing?.Contains(path) == true)
            {
                throw new HttpRequestException("connection reset");
            }

            // HttpClient reports its own timeout as a cancellation the caller never asked for.
            if (timingOut?.Contains(path) == true)
            {
                throw new TaskCanceledException("The request timed out.");
            }

            if (failing?.TryGetValue(path, out var status) == true)
            {
                return Task.FromResult(new HttpResponseMessage(status));
            }

            if (!pages.TryGetValue(path, out var page))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(page.Body) };
            if (page.Link is not null)
            {
                response.Headers.TryAddWithoutValidation("Link", page.Link);
            }

            return Task.FromResult(response);
        }
    }

    private static (GitHubRepoClient Client, PagingHandler Handler) Create(
        Dictionary<string, (string Body, string? Link)> pages,
        Dictionary<string, HttpStatusCode>? failing = null,
        HashSet<string>? throwing = null,
        HashSet<string>? timingOut = null)
    {
        var handler = new PagingHandler(pages, failing, throwing, timingOut);
        return (new GitHubRepoClient(new HttpClient(handler), BaseUrl), handler);
    }

    private static string Alerts(string prefix, int count) =>
        "[" + string.Join(",", Enumerable.Range(0, count).Select(i => $$"""
            {
              "security_advisory": { "ghsa_id": "GHSA-{{prefix}}-{{i}}", "cve_id": null, "severity": "high", "summary": "s" },
              "security_vulnerability": {
                "package": { "ecosystem": "npm", "name": "pkg-{{prefix}}-{{i}}" },
                "vulnerable_version_range": "< 1.0.0",
                "first_patched_version": { "identifier": "1.0.0" }
              },
              "html_url": "https://gh.test/a/{{prefix}}/{{i}}"
            }
            """)) + "]";

    // GitHub's next links keep state=open and percent-encode the cursor; both are kept here so a client
    // that dropped the filter or decoded the cursor would request a path this handler does not answer.
    private static string PagePath(string cursor) =>
        $"/repositories/42/dependabot/alerts?state=open&per_page=100&after={cursor}%3D";

    [Fact]
    public async Task Follows_next_links_until_the_last_page_and_reports_the_list_complete()
    {
        var (client, handler) = Create(new()
        {
            // GitHub writes the next URL against the numeric repository route, and lists other relations
            // beside it, so the parser has to pick rel="next" out of the set rather than take the first.
            [FirstPage] = (Alerts("a", 100),
                $"<{BaseUrl}{PagePath("c1")}>; rel=\"next\", <{BaseUrl}{FirstPage}>; rel=\"first\""),
            [PagePath("c1")] = (Alerts("b", 100),
                $"<{BaseUrl}{FirstPage}>; rel=\"prev\", <{BaseUrl}{PagePath("c2")}>; rel=\"next\""),
            [PagePath("c2")] = (Alerts("c", 22), $"<{BaseUrl}{PagePath("c1")}>; rel=\"prev\""),
        });

        var list = await client.ListDependabotAlertsAsync(Token, Repo);

        Assert.Equal(222, list.Alerts.Count);
        Assert.False(list.Truncated);
        Assert.Equal("GHSA-c-21", list.Alerts[^1].GhsaId);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(Token, request.Headers.Authorization?.Parameter);
        });
    }

    [Fact]
    public async Task Stops_at_the_page_ceiling_and_reports_the_list_truncated()
    {
        // Every page points at another, so only the ceiling ends the walk.
        var pages = new Dictionary<string, (string Body, string? Link)>
        {
            [FirstPage] = (Alerts("p0", 100), $"<{BaseUrl}{PagePath("1")}>; rel=\"next\""),
        };
        for (var page = 1; page <= GitHubRepoClient.MaxAlertPages + 1; page++)
        {
            pages[PagePath(page.ToString())] = (Alerts($"p{page}", 100), $"<{BaseUrl}{PagePath((page + 1).ToString())}>; rel=\"next\"");
        }

        var (client, handler) = Create(pages);

        var list = await client.ListDependabotAlertsAsync(Token, Repo);

        Assert.Equal(GitHubRepoClient.MaxAlertPages, handler.Requests.Count);
        Assert.Equal(GitHubRepoClient.MaxAlertPages * 100, list.Alerts.Count);
        Assert.True(list.Truncated);
    }

    [Fact]
    public async Task Never_sends_the_token_to_a_next_link_on_another_host_and_reports_the_list_truncated()
    {
        // A host that merely starts with the configured one is another host.
        var (client, handler) = Create(new()
        {
            [FirstPage] = (Alerts("a", 100), $"<https://gh.test.example{PagePath("c1")}>; rel=\"next\""),
        });

        var list = await client.ListDependabotAlertsAsync(Token, Repo);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("gh.test", request.RequestUri!.Host);
        Assert.Equal(100, list.Alerts.Count);
        Assert.True(list.Truncated);
    }

    [Fact]
    public async Task A_later_page_that_fails_keeps_the_earlier_pages_and_reports_the_list_truncated()
    {
        var (client, _) = Create(
            new() { [FirstPage] = (Alerts("a", 100), $"<{BaseUrl}{PagePath("c1")}>; rel=\"next\"") },
            failing: new() { [PagePath("c1")] = HttpStatusCode.Forbidden });

        var list = await client.ListDependabotAlertsAsync(Token, Repo);

        Assert.Equal(100, list.Alerts.Count);
        Assert.True(list.Truncated);
    }

    [Fact]
    public async Task A_later_page_lost_to_the_network_keeps_the_earlier_pages_and_reports_the_list_truncated()
    {
        var (client, _) = Create(
            new() { [FirstPage] = (Alerts("a", 100), $"<{BaseUrl}{PagePath("c1")}>; rel=\"next\"") },
            throwing: [PagePath("c1")]);

        var list = await client.ListDependabotAlertsAsync(Token, Repo);

        Assert.Equal(100, list.Alerts.Count);
        Assert.True(list.Truncated);
    }

    [Fact]
    public async Task A_first_page_that_fails_is_the_callers_failure()
    {
        // Nothing has been read, so there is no partial list to return: the scanner's caller decides.
        var (client, _) = Create([], failing: new() { [FirstPage] = HttpStatusCode.Forbidden });

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ListDependabotAlertsAsync(Token, Repo));
    }

    [Fact]
    public async Task Follows_a_relative_next_link_against_the_configured_host()
    {
        var (client, handler) = Create(new()
        {
            [FirstPage] = (Alerts("a", 100), $"<{PagePath("c1")}>; rel=\"next\""),
            [PagePath("c1")] = (Alerts("b", 3), null),
        });

        var list = await client.ListDependabotAlertsAsync(Token, Repo);

        Assert.Equal(103, list.Alerts.Count);
        Assert.False(list.Truncated);
        Assert.All(handler.Requests, request => Assert.Equal("gh.test", request.RequestUri!.Host));
    }

    [Fact]
    public async Task Does_not_follow_a_network_path_next_link()
    {
        var (client, handler) = Create(new()
        {
            [FirstPage] = (Alerts("a", 100), $"<//elsewhere.example{PagePath("c1")}>; rel=\"next\""),
        });

        var list = await client.ListDependabotAlertsAsync(Token, Repo);

        Assert.Single(handler.Requests);
        Assert.True(list.Truncated);
    }

    [Fact]
    public async Task Does_not_follow_a_next_link_that_stopped_asking_for_open_alerts()
    {
        // Following it would mix fixed and dismissed alerts into a list presented as open ones.
        var (client, handler) = Create(new()
        {
            [FirstPage] = (Alerts("a", 100), $"<{BaseUrl}/repositories/42/dependabot/alerts?per_page=100&after=c1>; rel=\"next\""),
        });

        var list = await client.ListDependabotAlertsAsync(Token, Repo);

        Assert.Single(handler.Requests);
        Assert.Equal(100, list.Alerts.Count);
        Assert.True(list.Truncated);
    }

    [Fact]
    public async Task A_later_page_that_times_out_keeps_the_earlier_pages_and_reports_the_list_truncated()
    {
        var (client, _) = Create(
            new() { [FirstPage] = (Alerts("a", 100), $"<{BaseUrl}{PagePath("c1")}>; rel=\"next\"") },
            timingOut: [PagePath("c1")]);

        var list = await client.ListDependabotAlertsAsync(Token, Repo);

        Assert.Equal(100, list.Alerts.Count);
        Assert.True(list.Truncated);
    }

    [Fact]
    public async Task A_caller_who_cancels_mid_walk_gets_the_cancellation_not_a_partial_list()
    {
        // Cancelled by the caller while page two is in flight, not timed out: the request that asked has
        // stopped waiting, so there is nobody to hand a partial list to.
        using var cancel = new CancellationTokenSource();
        var handler = new CancelOnSecondPage(cancel, Alerts("a", 100), $"<{BaseUrl}{PagePath("c1")}>; rel=\"next\"");
        var client = new GitHubRepoClient(new HttpClient(handler), BaseUrl);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ListDependabotAlertsAsync(Token, Repo, cancel.Token));
        Assert.Equal(2, handler.Requests);
    }

    private sealed class CancelOnSecondPage(CancellationTokenSource cancel, string firstBody, string link)
        : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (++Requests == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(firstBody) };
                response.Headers.TryAddWithoutValidation("Link", link);
                return response;
            }

            await cancel.CancelAsync();
            throw new TaskCanceledException("cancelled", null, cancel.Token);
        }
    }
}
