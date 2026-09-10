using System.Net;
using System.Text;
using System.Text.Json;
using Condux.Core.SourceControl;
using Condux.GitHub;
using Xunit;

namespace Condux.GitHub.Tests;

public class GitHubRepoClientTests
{
    private const string Token = "ghs_test";
    private const string Repo = "acme/api";

    // Routes each request by "METHOD path" to a canned response and records every request + body.
    private sealed class RoutingHandler(Dictionary<string, (HttpStatusCode Status, string Body)> routes)
        : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request, body));
            var key = $"{request.Method} {request.RequestUri!.PathAndQuery}";
            var (status, responseBody) = routes.TryGetValue(key, out var route)
                ? route
                : (HttpStatusCode.NotFound, """{"message":"Not Found"}""");
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }

    private static (GitHubRepoClient Client, RoutingHandler Handler) Create(
        Dictionary<string, (HttpStatusCode, string)> routes)
    {
        var handler = new RoutingHandler(routes);
        return (new GitHubRepoClient(new HttpClient(handler), "https://gh.test"), handler);
    }

    [Fact]
    public void Is_a_source_host_client_implementation()
    {
        // The GitHub client is one implementation of the neutral source-host seam (#145); a GitLab client
        // would be another. This guards the interface from being quietly dropped.
        Assert.IsAssignableFrom<ISourceHostClient>(new GitHubRepoClient(new HttpClient()));
    }

    [Fact]
    public async Task Reads_and_decodes_a_file_and_returns_null_when_absent()
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("const x = 1;\n"));
        var (client, handler) = Create(new()
        {
            [$"GET /repos/{Repo}/contents/src/x.js?ref=main"] =
                (HttpStatusCode.OK, $$"""{"content":"{{encoded}}","sha":"abc123"}"""),
        });

        var file = await client.GetFileAsync(Token, Repo, "src/x.js", "main");
        Assert.Equal("const x = 1;\n", file!.Content);
        Assert.Equal("abc123", file.Sha);
        Assert.Equal("Bearer", handler.Requests[0].Request.Headers.Authorization?.Scheme);

        Assert.Null(await client.GetFileAsync(Token, Repo, "src/missing.js", "main"));
    }

    [Fact]
    public async Task Creates_a_branch_from_the_base_head()
    {
        var (client, handler) = Create(new()
        {
            [$"GET /repos/{Repo}/git/ref/heads/main"] =
                (HttpStatusCode.OK, """{"object":{"sha":"base-sha"}}"""),
            [$"POST /repos/{Repo}/git/refs"] = (HttpStatusCode.Created, "{}"),
        });

        var sha = await client.GetBranchHeadShaAsync(Token, Repo, "main");
        await client.CreateBranchAsync(Token, Repo, "condux/fix-1", sha);

        Assert.Equal("base-sha", sha);
        using var body = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.Equal("refs/heads/condux/fix-1", body.RootElement.GetProperty("ref").GetString());
        Assert.Equal("base-sha", body.RootElement.GetProperty("sha").GetString());
    }

    [Fact]
    public async Task Puts_a_file_with_the_existing_sha_only_when_updating()
    {
        var (client, handler) = Create(new()
        {
            [$"PUT /repos/{Repo}/contents/src/x.js"] = (HttpStatusCode.OK, "{}"),
            [$"PUT /repos/{Repo}/contents/src/new.js"] = (HttpStatusCode.Created, "{}"),
        });

        await client.PutFileAsync(Token, Repo, "src/x.js", "b", "msg", "new contents", existingSha: "abc123");
        await client.PutFileAsync(Token, Repo, "src/new.js", "b", "msg", "fresh", existingSha: null);

        using var update = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.Equal("abc123", update.RootElement.GetProperty("sha").GetString());
        Assert.Equal(
            "new contents",
            Encoding.UTF8.GetString(Convert.FromBase64String(
                update.RootElement.GetProperty("content").GetString()!)));

        // Creating a new file must omit sha entirely (GitHub rejects null).
        using var create = JsonDocument.Parse(handler.Requests[1].Body);
        Assert.False(create.RootElement.TryGetProperty("sha", out _));
    }

    [Fact]
    public async Task Lists_branch_names_for_the_base_branch_picker()
    {
        var (client, handler) = Create(new()
        {
            [$"GET /repos/{Repo}/branches?per_page=100"] =
                (HttpStatusCode.OK, """[{"name":"main"},{"name":"develop"},{"name":"release/1.4"}]"""),
        });

        var branches = await client.ListBranchesAsync(Token, Repo);

        Assert.Equal(["main", "develop", "release/1.4"], branches);
        Assert.Equal("Bearer", handler.Requests[0].Request.Headers.Authorization?.Scheme);
    }

    // Scoped by the installation token, so this answers "what may we touch?" rather than "what exists".
    // Linking anything outside it would look fine and only fail when a fix run tried to reach the repo.
    [Fact]
    public async Task Lists_the_repositories_the_installation_can_reach()
    {
        var (client, handler) = Create(new()
        {
            ["GET /installation/repositories?per_page=100"] = (HttpStatusCode.OK, """
                {"total_count":2,"repositories":[
                  {"full_name":"acme/checkout"},{"full_name":"acme/billing"}]}
                """),
        });

        var repositories = await client.ListInstallationRepositoriesAsync(Token);

        Assert.Equal(["acme/checkout", "acme/billing"], repositories);
        Assert.Equal("Bearer", handler.Requests[0].Request.Headers.Authorization?.Scheme);
    }

    // An installation with no repositories selected is a valid state, not a failure.
    [Fact]
    public async Task Reports_no_repositories_when_the_installation_has_none()
    {
        var (client, _) = Create(new()
        {
            ["GET /installation/repositories?per_page=100"] =
                (HttpStatusCode.OK, """{"total_count":0,"repositories":[]}"""),
        });

        Assert.Empty(await client.ListInstallationRepositoriesAsync(Token));
    }

    [Fact]
    public async Task Lists_open_dependabot_alerts_with_package_severity_and_fixed_version()
    {
        var (client, handler) = Create(new()
        {
            [$"GET /repos/{Repo}/dependabot/alerts?state=open&per_page=100"] = (HttpStatusCode.OK, """
                [{
                  "security_advisory": {
                    "ghsa_id": "GHSA-jf85-cpcp-j695", "cve_id": "CVE-2019-10744",
                    "severity": "high", "summary": "Prototype pollution in lodash"
                  },
                  "security_vulnerability": {
                    "package": { "ecosystem": "npm", "name": "lodash" },
                    "vulnerable_version_range": "< 4.17.12",
                    "first_patched_version": { "identifier": "4.17.12" }
                  },
                  "dependency": { "manifest_path": "web/package.json", "scope": "runtime" },
                  "html_url": "https://gh.test/acme/api/security/dependabot/1"
                },{
                  "security_advisory": {
                    "ghsa_id": "GHSA-aaaa-bbbb-cccc", "cve_id": null,
                    "severity": "low", "summary": "No dependency block"
                  },
                  "security_vulnerability": {
                    "package": { "ecosystem": "npm", "name": "left-pad" },
                    "vulnerable_version_range": "< 1.3.0",
                    "first_patched_version": null
                  },
                  "html_url": "https://gh.test/acme/api/security/dependabot/2"
                }]
                """),
        });

        var alerts = await client.ListDependabotAlertsAsync(Token, Repo);
        var alert = alerts[0];
        Assert.Equal("GHSA-jf85-cpcp-j695", alert.GhsaId);
        Assert.Equal("CVE-2019-10744", alert.CveId);
        Assert.Equal("high", alert.Severity);
        Assert.Equal("lodash", alert.Package);
        Assert.Equal("npm", alert.Ecosystem);
        Assert.Equal("< 4.17.12", alert.VulnerableRange);
        Assert.Equal("4.17.12", alert.FixedVersion);
        // The manifest the dependency is declared in (#41 — points a bump at a monorepo member); an
        // alert without the optional dependency block reads as unknown, never as an error.
        Assert.Equal("web/package.json", alert.ManifestPath);
        Assert.Null(alerts[1].ManifestPath);
        Assert.Equal("Bearer", handler.Requests[0].Request.Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task Lists_the_recursive_file_tree_keeping_only_blobs()
    {
        var (client, handler) = Create(new()
        {
            [$"GET /repos/{Repo}/git/trees/main?recursive=1"] = (HttpStatusCode.OK, """
                {"tree":[
                  {"path":"src","type":"tree"},
                  {"path":"src/checkout.js","type":"blob"},
                  {"path":"src/cart.js","type":"blob"}
                ],"truncated":false}
                """),
        });

        var files = await client.ListTreeAsync(Token, Repo, "main");

        Assert.Equal(["src/checkout.js", "src/cart.js"], files); // directory entry dropped
        Assert.Equal("Bearer", handler.Requests[0].Request.Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task Finds_the_latest_commit_touching_the_culprit_file_as_of_a_ref()
    {
        var (client, handler) = Create(new()
        {
            // path is URL-encoded (the slash becomes %2F); sha scopes the history to the release commit.
            [$"GET /repos/{Repo}/commits?path=src%2Fcheckout.js&sha=rel-sha&per_page=1"] = (HttpStatusCode.OK, """
                [{
                  "sha": "def456",
                  "html_url": "https://gh.test/acme/api/commit/def456",
                  "commit": { "message": "Refactor charge()\n\nlong body text", "author": { "name": "Jane" } },
                  "author": { "login": "janedev" }
                }]
                """),
        });

        var suspect = await client.GetLatestCommitTouchingAsync(Token, Repo, "src/checkout.js", "rel-sha");

        Assert.Equal("def456", suspect!.Sha);
        Assert.Equal("Refactor charge()", suspect.Subject); // subject line only, body dropped
        Assert.Equal("janedev", suspect.Author);
        Assert.Equal("https://gh.test/acme/api/commit/def456", suspect.HtmlUrl);
        Assert.Equal("Bearer", handler.Requests[0].Request.Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task Suspect_commit_author_is_null_when_the_committer_has_no_github_account()
    {
        // GitHub returns the top-level author as an empty object when the commit author is not a GitHub user.
        var (client, _) = Create(new()
        {
            [$"GET /repos/{Repo}/commits?path=src%2Fx.js&sha=rel-sha&per_page=1"] = (HttpStatusCode.OK, """
                [{"sha":"a1","html_url":"https://gh.test/c/a1","commit":{"message":"tweak"},"author":{}}]
                """),
        });

        var suspect = await client.GetLatestCommitTouchingAsync(Token, Repo, "src/x.js", "rel-sha");

        Assert.Equal("a1", suspect!.Sha);
        Assert.Null(suspect.Author);
    }

    [Fact]
    public async Task Suspect_commit_is_null_when_the_file_has_no_history_at_the_ref()
    {
        var (client, _) = Create(new()
        {
            [$"GET /repos/{Repo}/commits?path=src%2Fnew.js&sha=rel-sha&per_page=1"] = (HttpStatusCode.OK, "[]"),
        });

        Assert.Null(await client.GetLatestCommitTouchingAsync(Token, Repo, "src/new.js", "rel-sha"));
    }

    [Fact]
    public async Task Opens_a_draft_pull_request_and_returns_its_url()
    {
        var (client, handler) = Create(new()
        {
            [$"POST /repos/{Repo}/pulls"] =
                (HttpStatusCode.Created, """{"html_url":"https://gh.test/acme/api/pull/7"}"""),
        });

        var url = await client.OpenDraftPullRequestAsync(Token, Repo, "condux/fix-1", "main", "title", "body");

        Assert.Equal("https://gh.test/acme/api/pull/7", url);
        using var body = JsonDocument.Parse(handler.Requests[0].Body);
        Assert.True(body.RootElement.GetProperty("draft").GetBoolean()); // draft-only, never ready-for-review
        Assert.Equal("condux/fix-1", body.RootElement.GetProperty("head").GetString());
        Assert.Equal("main", body.RootElement.GetProperty("base").GetString());
    }

    // ---- What reaches the URL -------------------------------------------------------------------
    //
    // The paths this client is given come from a model's fix plan or from an ingested stack frame's
    // filename, so they are strings someone else authored, and they used to be interpolated raw. .NET's
    // Uri collapses dot segments before the request is sent, so a path could move the request off the
    // /contents/ namespace entirely while carrying a token that can read and write the repository.
    //
    // These assert on the URI the handler SAW. Asserting on the return value would pass just as well
    // against the vulnerable code, because a traversal request that reaches GitHub simply 404s and
    // GetFileAsync answers null either way.

    [Theory]
    [InlineData("../../other-repo/secrets.txt")]
    [InlineData("src/../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("..")]
    public async Task A_path_that_leaves_the_repo_root_is_refused_before_any_request_is_sent(string path)
    {
        var (client, handler) = Create([]);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetFileAsync(Token, Repo, path, "main"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.PutFileAsync(Token, Repo, path, "branch", "msg", "content", existingSha: null));

        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// Encoding is a separate defence from containment and catches a different thing: these paths are
    /// legal inside a repository, and left raw they would end the path and start a query or a fragment.
    /// </summary>
    [Theory]
    [InlineData("src/my file.js", "/repos/acme/api/contents/src/my%20file.js?ref=main")]
    [InlineData("src/a#b.js", "/repos/acme/api/contents/src/a%23b.js?ref=main")]
    [InlineData("src/a?b.js", "/repos/acme/api/contents/src/a%3Fb.js?ref=main")]
    public async Task An_awkward_but_legal_filename_is_encoded_rather_than_changing_the_request(
        string path, string expectedPathAndQuery)
    {
        var (client, handler) = Create([]);

        await client.GetFileAsync(Token, Repo, path, "main");

        Assert.Equal(expectedPathAndQuery, handler.Requests[0].Request.RequestUri!.PathAndQuery);
    }

    /// <summary>
    /// The other half of the same fix, and the one that would break the product if it were wrong: a repo
    /// path's separators are structure, so a nested path must still address the file it names. Encoding
    /// the whole string in one call would produce src%2Fapp%2FProgram.cs and resolve to nothing.
    /// </summary>
    [Fact]
    public async Task A_nested_path_keeps_its_separators()
    {
        var (client, handler) = Create([]);

        await client.GetFileAsync(Token, Repo, "src/app/Program.cs", "main");

        Assert.Equal(
            "/repos/acme/api/contents/src/app/Program.cs?ref=main",
            handler.Requests[0].Request.RequestUri!.PathAndQuery);
    }

    /// <summary>
    /// The repository name and the branch traverse the URL just as a file path does, and neither is
    /// validated where it is stored: `RepoEndpoints` writes `RepoFullName` and `DefaultBranch` with no
    /// shape check, and the base branch of a fix run is chosen per request. Escaping alone does not stop
    /// it, since `..` is unreserved and survives percent-encoding.
    /// </summary>
    [Theory]
    [InlineData("acme/api/../../other", "main")]
    [InlineData("acme/api", "../../other")]
    public async Task A_repo_or_branch_that_traverses_the_url_is_refused_before_any_request(
        string repoFullName, string branch)
    {
        var (client, handler) = Create([]);

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetBranchHeadShaAsync(Token, repoFullName, branch));

        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// A repository is owner/name and a branch may be feature/x, so both keep their separators.
    /// </summary>
    [Fact]
    public async Task A_repo_and_a_branch_keep_their_separators_and_encode_the_rest()
    {
        var (client, handler) = Create(new()
        {
            ["GET /repos/acme/api/git/ref/heads/feature/new%20thing"] =
                (HttpStatusCode.OK, """{"object":{"sha":"base-sha"}}"""),
        });

        // Stubbed at the escaped URL and asserted on the returned sha, so the request has to have landed
        // exactly there. Letting the call throw on an unstubbed route would pass for any wrong URL.
        var sha = await client.GetBranchHeadShaAsync(Token, "acme/api", "feature/new thing");

        Assert.Equal("base-sha", sha);
        Assert.Equal(
            "/repos/acme/api/git/ref/heads/feature/new%20thing",
            handler.Requests[0].Request.RequestUri!.PathAndQuery);
    }
}
