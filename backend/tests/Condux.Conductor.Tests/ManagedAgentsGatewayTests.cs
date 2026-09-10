using System.Net;
using System.Text.Json;
using Condux.Agent.ManagedAgents;
using Condux.Core.FixEngine;
using Condux.GitHub;
using Xunit;

namespace Condux.Conductor.Tests;

/// <summary>
/// The vendor-hosted gateway (ADR-0038), stub-HTTP on both sides (CI never calls a real service). The
/// load-bearing assertions: the session's repo mount carries the READ-ONLY downscoped token and a
/// budget (and the full-permission token appears nowhere in any vendor request), an idle session's
/// final plan becomes a draft PR through host-side git, and every refusal path — vendor failure,
/// unusable final message, path escape, throttling — lands as the right run state.
/// </summary>
public sealed class ManagedAgentsGatewayTests
{
    private const string Repo = "acme/app";


    private static (ManagedAgentsGateway Gateway, StubHttpHandler Cma, StubHttpHandler GitHub) Create()
    {
        var cma = new StubHttpHandler();
        var github = new StubHttpHandler();
        var options = new GitHubAppOptions(
            "Iv1.client", System.Security.Cryptography.RSA.Create(2048).ExportRSAPrivateKeyPem(), "whsec")
        { ApiBaseUrl = "https://gh.test" };
        var tokens = new GitHubInstallationTokens(
            new HttpClient(github), options, () => DateTimeOffset.UnixEpoch.AddYears(56));
        var repoClient = new GitHubRepoClient(new HttpClient(github), "https://gh.test");
        var gateway = new ManagedAgentsGateway(
            new ManagedAgentsClient(new HttpClient(cma), new ManagedAgentsOptions("cma-key", 5m)),
            tokens, repoClient, new ManagedAgentsOptions("cma-key", 5m));
        return (gateway, cma, github);
    }

    private static AgentRunSpec Spec() =>
        new("iss-1", Repo, "main", "Fix the null deref in checkout.", "claude-opus-4-8")
        {
            ScopedPaths = ["src/cart.js"],
            InstallationId = 7,
            OrgId = 3,
        };

    private static void StubStart(StubHttpHandler cma, StubHttpHandler github)
    {
        // Two token mints ride distinct cache keys: the read-only one for the session mount, later the
        // full one for git. One stub serves both; the bodies tell them apart in the assertions.
        github.Routes["POST /app/installations/7/access_tokens"] =
            (HttpStatusCode.Created, """{"token":"ghs_tok","expires_at":"2099-01-01T00:00:00Z"}""");
        cma.Routes["GET /v1/agents"] = (HttpStatusCode.OK, """{"data":[]}""");
        cma.Routes["GET /v1/environments"] = (HttpStatusCode.OK, """{"data":[]}""");
        cma.Routes["POST /v1/agents"] = (HttpStatusCode.OK, """{"id":"agent_1","name":"condux-conductor-claude-opus-4-8","archived_at":null}""");
        cma.Routes["POST /v1/environments"] = (HttpStatusCode.OK, """{"id":"env_1","name":"condux-conductor","state":"active"}""");
        cma.Routes["POST /v1/sessions"] = (HttpStatusCode.OK, """{"id":"sesn_1","status":"running"}""");
    }

    private static string IdleSession(string usage =
        """{"input_tokens":4,"output_tokens":685,"cache_read_input_tokens":8153,"cache_creation":{"ephemeral_1h_input_tokens":0,"ephemeral_5m_input_tokens":8398},"list_cost":{"amount":"7","currency":"USD"}}""") =>
        $$"""{"id":"sesn_1","status":"idle","usage":{{usage}}}""";

    private static string FinalMessageEvents(string planJson) => JsonSerializer.Serialize(new
    {
        data = new object[]
        {
            new { type = "user.message", content = new[] { new { type = "text", text = "fix it" } } },
            new { type = "agent.message", content = new[] { new { type = "text", text = planJson } } },
        },
    });

    [Fact]
    public async Task Start_mounts_the_repo_with_the_read_only_token_and_a_budget()
    {
        var (gateway, cma, github) = Create();
        StubStart(cma, github);

        var run = await gateway.StartAsync(Spec());

        Assert.Equal("sesn_1", run.RunId);
        // The mint that produced the session token was the DOWNSCOPED one.
        var mint = github.Requests.Single(r => r.Key.Contains("access_tokens"));
        Assert.Contains("\"contents\":\"read\"", mint.Body);
        Assert.Contains("\"app\"", mint.Body); // repositories filter carries the bare repo name

        var create = JsonDocument.Parse(cma.Requests.Single(r => r.Key == "POST /v1/sessions").Body).RootElement;
        var resource = create.GetProperty("resources")[0];
        Assert.Equal($"https://github.com/{Repo}", resource.GetProperty("url").GetString());
        Assert.Equal("ghs_tok", resource.GetProperty("authorization_token").GetString());
        Assert.Equal("main", resource.GetProperty("checkout").GetProperty("name").GetString());
        Assert.Equal("500", create.GetProperty("budget").GetProperty("max_list_cost").GetProperty("amount").GetString());
        var message = create.GetProperty("initial_events")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("Fix the null deref", message);
        Assert.Contains("src/cart.js", message);
    }

    [Fact]
    public async Task An_idle_session_with_a_fix_plan_becomes_a_draft_pr_with_usage_mapped()
    {
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Routes["GET /v1/sessions/sesn_1"] = (HttpStatusCode.OK, IdleSession());
        cma.Routes["GET /v1/sessions/sesn_1/events?limit=100"] = (HttpStatusCode.OK, FinalMessageEvents(
            """Here is the fix. {"summary":"Guard the null cart","files":[{"path":"src/cart.js","contents":"fixed"}]}"""));
        cma.Routes["DELETE /v1/sessions/sesn_1"] = (HttpStatusCode.OK, """{"id":"sesn_1","type":"session_deleted"}""");
        github.Routes[$"GET /repos/{Repo}/git/ref/heads/main"] =
            (HttpStatusCode.OK, """{"object":{"sha":"base-sha"}}""");
        github.Routes[$"POST /repos/{Repo}/git/refs"] = (HttpStatusCode.Created, "{}");
        github.Routes[$"GET /repos/{Repo}/contents/src/cart.js?ref=main"] =
            (HttpStatusCode.OK, """{"path":"src/cart.js","sha":"old-sha","content":"","encoding":"base64"}""");
        github.Routes[$"PUT /repos/{Repo}/contents/src/cart.js"] = (HttpStatusCode.OK, "{}");
        github.Routes[$"POST /repos/{Repo}/pulls"] =
            (HttpStatusCode.Created, """{"html_url":"https://gh.test/acme/app/pull/9"}""");

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.True(progress.Status == AgentRunStatus.Succeeded, progress.Error);
        Assert.Equal("https://gh.test/acme/app/pull/9", progress.PrUrl);
        Assert.StartsWith("condux/fix-iss-1-", progress.Branch);
        Assert.Equal("Guard the null cart", progress.Summary);
        // Cache tokens fold into input (they dominate a CMA session; probed live).
        Assert.Equal(4 + 8153 + 8398, progress.InputTokens);
        Assert.Equal(685, progress.OutputTokens);
        var pr = JsonDocument.Parse(github.Requests.Single(r => r.Key == $"POST /repos/{Repo}/pulls").Body).RootElement;
        Assert.True(pr.GetProperty("draft").GetBoolean());
        // The finalized run is cleaned up vendor-side and memoized locally.
        Assert.Contains(cma.Requests, r => r.Key == "DELETE /v1/sessions/sesn_1");
        var again = await gateway.PollAsync(run.RunId);
        Assert.Equal(progress.PrUrl, again.PrUrl);
    }

    [Fact]
    public async Task A_running_session_reports_still_running_and_a_throttled_poll_does_too()
    {
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Sequences["GET /v1/sessions/sesn_1"] = new Queue<(HttpStatusCode, string)>([
            (HttpStatusCode.OK, """{"id":"sesn_1","status":"running"}"""),
            (HttpStatusCode.TooManyRequests, """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}"""),
        ]);

        var run = await gateway.StartAsync(Spec());
        Assert.Equal(AgentRunStatus.Running, (await gateway.PollAsync(run.RunId)).Status);
        Assert.Equal(AgentRunStatus.Running, (await gateway.PollAsync(run.RunId)).Status);
    }

    [Fact]
    public async Task A_vendor_side_failure_state_fails_the_run_with_the_vendor_status()
    {
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Routes["GET /v1/sessions/sesn_1"] = (HttpStatusCode.OK, """{"id":"sesn_1","status":"failed"}""");

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, progress.Status);
        Assert.Contains("failed", progress.Error);
    }

    [Fact]
    public async Task An_unusable_final_message_fails_the_run_and_opens_nothing()
    {
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Routes["GET /v1/sessions/sesn_1"] = (HttpStatusCode.OK, IdleSession());
        cma.Routes["GET /v1/sessions/sesn_1/events?limit=100"] = (HttpStatusCode.OK, FinalMessageEvents(
            "I could not determine a fix."));

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, progress.Status);
        Assert.DoesNotContain(github.Requests, r => r.Key.Contains("/pulls"));
    }

    [Fact]
    public async Task A_plan_path_escaping_the_repository_is_refused_before_any_git()
    {
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Routes["GET /v1/sessions/sesn_1"] = (HttpStatusCode.OK, IdleSession());
        cma.Routes["GET /v1/sessions/sesn_1/events?limit=100"] = (HttpStatusCode.OK, FinalMessageEvents(
            """{"summary":"evil","files":[{"path":"../../etc/passwd","contents":"x"}]}"""));

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, progress.Status);
        // The offending path, not either module's wording: the rule moved from this gateway into the
        // shared RepoPaths and its message came with it, and a test pinned to a phrase would have read
        // as a regression when nothing about the behaviour changed.
        Assert.Contains("../../etc/passwd", progress.Error);
        Assert.DoesNotContain(github.Requests, r => r.Key.Contains("/git/") || r.Key.Contains("/pulls"));
    }

    [Fact]
    public async Task Installation_zero_is_refused_up_front()
    {
        var (gateway, _, _) = Create();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.StartAsync(Spec() with { InstallationId = 0 }));
    }

    [Fact]
    public async Task An_existing_unarchived_agent_is_reused_not_recreated()
    {
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Routes["GET /v1/agents"] = (HttpStatusCode.OK,
            """{"data":[{"id":"agent_old","name":"condux-conductor-claude-opus-4-8","archived_at":null}]}""");

        await gateway.StartAsync(Spec());

        Assert.DoesNotContain(cma.Requests, r => r.Key == "POST /v1/agents");
        var create = JsonDocument.Parse(cma.Requests.Single(r => r.Key == "POST /v1/sessions").Body).RootElement;
        Assert.Equal("agent_old", create.GetProperty("agent").GetString());
    }

    [Fact]
    public async Task Unknown_run_ids_throw()
    {
        var (gateway, _, _) = Create();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => gateway.PollAsync("sesn_nope"));
    }
}
