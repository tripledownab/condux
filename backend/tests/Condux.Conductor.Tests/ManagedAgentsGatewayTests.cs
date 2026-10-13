using System.Net;
using System.Text;
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

    // The event read asks for the newest first, since the gateway needs the latest of them.
    private const string EventsRoute = "GET /v1/sessions/sesn_1/events?limit=100&order=desc";

    // The environment list asks for 100 a page, since the default of 20 would miss ours on a busy key.
    private const string EnvironmentsRoute = "GET /v1/environments?limit=100";


    private static (ManagedAgentsGateway Gateway, StubHttpHandler Cma, StubHttpHandler GitHub) Create(
        string? pinnedAgentId = null, string? pinnedEnvironmentId = null)
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
            tokens, repoClient, new ManagedAgentsOptions("cma-key", 5m) { AgentId = pinnedAgentId, EnvironmentId = pinnedEnvironmentId });
        return (gateway, cma, github);
    }

    // The session sets the system prompt itself, so the agent's own copy, which is whatever it was last
    // given, never decides it. Tools are set only on an agent found by name or created: a pinned agent keeps
    // the tools its operator chose, and the model is never overridden.
    private static void AssertSessionRunsAs(StubHttpHandler cma, string agentId, bool setsTools)
    {
        var create = JsonDocument.Parse(cma.Requests.Single(r => r.Key == "POST /v1/sessions").Body).RootElement;
        var agent = create.GetProperty("agent");
        Assert.Equal("agent_with_overrides", agent.GetProperty("type").GetString());
        Assert.Equal(agentId, agent.GetProperty("id").GetString());
        Assert.Equal(ManagedAgentsProvisioner.SystemPrompt, agent.GetProperty("system").GetString());
        Assert.False(agent.TryGetProperty("model", out _));
        Assert.Equal(setsTools, agent.TryGetProperty("tools", out var tools));
        if (setsTools)
        {
            AssertToolsetWithReadAndWebOff(tools);
        }
    }

    // The one toolset, off by default with five tools named back on. `read` stays off: with it on, the
    // platform announces every skill in the mounted repository's root `.claude/skills` to the agent as an
    // instruction. The two web tools stay off: they reach any host whatever the environment's networking says.
    private static void AssertToolsetWithReadAndWebOff(JsonElement tools)
    {
        var toolset = Assert.Single(tools.EnumerateArray());
        Assert.Equal("agent_toolset_20260401", toolset.GetProperty("type").GetString());
        Assert.False(toolset.GetProperty("default_config").GetProperty("enabled").GetBoolean());
        var configs = toolset.GetProperty("configs").EnumerateArray().ToList();
        Assert.Equal(["bash", "edit", "write", "glob", "grep"], configs.Select(c => c.GetProperty("name").GetString()));
        Assert.All(configs, c => Assert.True(c.GetProperty("enabled").GetBoolean()));
    }

    // An environment as the vendor answers once it has stored the policy.
    private static string EnvironmentWithNoEgress(string id, string networking =
        """{"type":"limited","allowed_hosts":[],"allow_package_managers":false,"allow_mcp_servers":false}""") =>
        $$$"""{"id":"{{{id}}}","name":"condux-conductor","archived_at":null,"config":{"type":"cloud","networking":{{{networking}}}}}""";

    // The sandbox's networking allows no host: limited networking with nothing allowed, registries and MCP servers
    // included. The vendor reads an omitted networking block as unrestricted.
    private static void AssertNoEgress(JsonElement config)
    {
        Assert.Equal("cloud", config.GetProperty("type").GetString());
        var networking = config.GetProperty("networking");
        Assert.Equal("limited", networking.GetProperty("type").GetString());
        Assert.Empty(networking.GetProperty("allowed_hosts").EnumerateArray());
        Assert.False(networking.GetProperty("allow_package_managers").GetBoolean());
        Assert.False(networking.GetProperty("allow_mcp_servers").GetBoolean());
    }

    private static AgentRunSpec Spec() =>
        new("iss-1", Repo, "main", "Fix the null deref in checkout.", "claude-opus-4-8")
        {
            RunId = Guid.CreateVersion7(),
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
        cma.Routes["GET /v1/agents?limit=100&include_archived=true"] = (HttpStatusCode.OK, """{"data":[]}""");
        cma.Routes[EnvironmentsRoute] = (HttpStatusCode.OK, """{"data":[]}""");
        cma.Routes["POST /v1/agents"] = (HttpStatusCode.OK, """{"id":"agent_1","name":"condux-conductor-claude-opus-4-8","archived_at":null}""");
        cma.Routes["POST /v1/environments"] = (HttpStatusCode.OK, EnvironmentWithNoEgress("env_1"));
        cma.Routes["POST /v1/sessions"] = (HttpStatusCode.OK, """{"id":"sesn_1","status":"running"}""");
        // The session clones the branch head as it stands at start, by commit.
        github.Routes[$"GET /repos/{Repo}/git/ref/heads/main"] =
            (HttpStatusCode.OK, """{"object":{"sha":"base-sha"}}""");
    }

    // A session that finished with a fix plan, and a GitHub that accepts the draft PR for it.
    private static void StubFinishedRun(StubHttpHandler cma, StubHttpHandler github)
    {
        StubStart(cma, github);
        cma.Routes["GET /v1/sessions/sesn_1"] = (HttpStatusCode.OK, IdleSession());
        cma.Routes[EventsRoute] = (HttpStatusCode.OK, FinalMessageEvents(UsablePlan));
        cma.Routes["DELETE /v1/sessions/sesn_1"] = (HttpStatusCode.OK, """{"id":"sesn_1","type":"session_deleted"}""");
        github.Routes[$"POST /repos/{Repo}/git/refs"] = (HttpStatusCode.Created, "{}");
        // The file as the session cloned it and as it is on the branch now: unchanged.
        github.Routes[$"GET /repos/{Repo}/contents/src/cart.js?ref=base-sha"] =
            (HttpStatusCode.OK, GitHubContents.ToJson(""));
        github.Routes[$"GET /repos/{Repo}/contents/src/cart.js?ref=main"] =
            (HttpStatusCode.OK, GitHubContents.ToJson(""));
        github.Routes[$"PUT /repos/{Repo}/contents/src/cart.js"] = (HttpStatusCode.OK, "{}");
        github.Routes[$"POST /repos/{Repo}/pulls"] =
            (HttpStatusCode.Created, """{"html_url":"https://gh.test/acme/app/pull/9"}""");
        // No pull request from the run's branch yet, so the publisher opens one.
        github.PathRoutes[$"GET /repos/{Repo}/pulls"] = (HttpStatusCode.OK, "[]");
    }

    private static string IdleSession(string usage =
        """{"input_tokens":4,"output_tokens":685,"cache_read_input_tokens":8153,"cache_creation":{"ephemeral_1h_input_tokens":0,"ephemeral_5m_input_tokens":8398},"list_cost":{"amount":"7","currency":"USD"}}""") =>
        $$"""{"id":"sesn_1","status":"idle","usage":{{usage}}}""";

    // The events of a session whose last message is the plan, with the idle event that says why it
    // stopped, as the read asks for them: newest first.
    private static string FinalMessageEvents(string planJson, string stopReason = "end_turn") =>
        JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                new { type = "session.status_idle", stop_reason = new { type = stopReason } },
                new { type = "agent.message", content = new[] { new { type = "text", text = planJson } } },
                new { type = "agent.message", content = new[] { new { type = "text", text = "Looking at the cart." } } },
            },
        });

    private const string UsablePlan =
        """Here is the fix. {"summary":"Guard the null cart","files":[{"path":"src/cart.js","contents":"fixed"}]}""";

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
        // Pinned to the commit the branch pointed at, so the run knows what the session saw.
        var checkout = resource.GetProperty("checkout");
        Assert.Equal("commit", checkout.GetProperty("type").GetString());
        Assert.Equal("base-sha", checkout.GetProperty("sha").GetString());
        Assert.False(checkout.TryGetProperty("name", out _));
        Assert.Equal("500", create.GetProperty("budget").GetProperty("max_list_cost").GetProperty("amount").GetString());
        var message = create.GetProperty("initial_events")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        Assert.Contains("Fix the null deref", message);
        Assert.Contains("src/cart.js", message);
    }

    // The session cloned the branch at one commit; someone changed the file on the branch before the session
    // finished. Publishing the session's version would quietly undo that change, so the run is refused.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_file_changed_or_added_upstream_during_the_session_is_not_replaced(bool existedAtClone)
    {
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        if (existedAtClone)
        {
            github.Routes[$"GET /repos/{Repo}/contents/src/cart.js?ref=main"] =
                (HttpStatusCode.OK, GitHubContents.ToJson("changed upstream\n"));
        }
        else
        {
            github.Routes.Remove($"GET /repos/{Repo}/contents/src/cart.js?ref=base-sha");
        }

        var run = await gateway.StartAsync(Spec());
        var done = await gateway.PollAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, done.Status);
        Assert.Equal(FixFailureReason.NoUsableFix, done.Reason);
        Assert.DoesNotContain(github.Requests, r => r.Key.StartsWith("PUT ", StringComparison.Ordinal));
        Assert.DoesNotContain(github.Requests, r => r.Key == $"POST /repos/{Repo}/git/refs");
    }

    // The file was there when the session cloned and someone deleted it on the branch since. Writing the
    // session's version would put it back, so the run is refused.
    [Fact]
    public async Task A_file_deleted_upstream_during_the_session_is_not_put_back()
    {
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        github.Routes.Remove($"GET /repos/{Repo}/contents/src/cart.js?ref=main");

        var run = await gateway.StartAsync(Spec());
        var done = await gateway.PollAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, done.Status);
        Assert.Equal(FixFailureReason.NoUsableFix, done.Reason);
        Assert.DoesNotContain(github.Requests, r => r.Key.StartsWith("PUT ", StringComparison.Ordinal));
    }

    // A path that was a link at the cloned commit is not a file the session could have read as itself, so
    // it is not counted as seen, and writing it fails as an unusable fix rather than an unclassified error.
    [Fact]
    public async Task A_link_at_the_cloned_commit_is_not_seen_and_fails_as_unusable()
    {
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        var target = Convert.ToBase64String(Encoding.UTF8.GetBytes("the target's text\n"));
        var link = $$"""{"type":"file","encoding":"base64","content":"{{target}}","sha":"513bc0bea80652bcd4583ea0071b5cb96d172fa0"}""";
        github.Routes[$"GET /repos/{Repo}/contents/src/cart.js?ref=base-sha"] = (HttpStatusCode.OK, link);
        github.Routes[$"GET /repos/{Repo}/contents/src/cart.js?ref=main"] = (HttpStatusCode.OK, link);

        var run = await gateway.StartAsync(Spec());
        var done = await gateway.PollAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, done.Status);
        Assert.Equal(FixFailureReason.NoUsableFix, done.Reason);
        Assert.DoesNotContain(github.Requests, r => r.Key.StartsWith("PUT ", StringComparison.Ordinal));
    }

    // The session is created only once the commit it will clone is known; a branch that cannot be read
    // leaves no session behind to spend.
    [Fact]
    public async Task A_branch_head_that_cannot_be_read_starts_no_session()
    {
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        github.Routes.Remove($"GET /repos/{Repo}/git/ref/heads/main");

        await Assert.ThrowsAnyAsync<Exception>(() => gateway.StartAsync(Spec()));

        Assert.DoesNotContain(cma.Requests, r => r.Key == "POST /v1/sessions");
    }

    [Fact]
    public async Task An_idle_session_with_a_fix_plan_becomes_a_draft_pr_with_usage_mapped()
    {
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);

        var spec = Spec();
        var run = await gateway.StartAsync(spec);
        var progress = await gateway.PollAsync(run.RunId);

        Assert.True(progress.Status == AgentRunStatus.Succeeded, progress.Error);
        Assert.Equal("https://gh.test/acme/app/pull/9", progress.PrUrl);
        // Named from the fix run's id, not the session's, which the run row does not keep.
        Assert.Equal(FixBranches.ToBranch("iss-1", spec.RunId), progress.Branch);
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
    public async Task A_cleanup_that_times_out_after_the_pr_is_opened_leaves_the_run_succeeded()
    {
        // The pull request exists before the session is deleted, so a timed-out delete is clutter on the
        // vendor's side. Escaping would fail the run and refund an allowance that was spent.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Failures["DELETE /v1/sessions/sesn_1"] = new TaskCanceledException("timed out");

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.True(progress.Status == AgentRunStatus.Succeeded, progress.Error);
        Assert.Equal("https://gh.test/acme/app/pull/9", progress.PrUrl);
    }

    [Fact]
    public async Task A_timeout_while_publishing_the_answer_fails_the_run_rather_than_escaping()
    {
        // The host-side git calls run under the caller's token, so HttpClient's own timeout arrives as a
        // cancellation nobody asked for. It used to escape the poll as if the worker were shutting down;
        // it is a failure of this run like any other publish failure.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        github.Failures[$"POST /repos/{Repo}/pulls"] = new TaskCanceledException("timed out");

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, progress.Status);
        Assert.Equal(AgentRunStatus.Failed, (await gateway.PollAsync(run.RunId)).Status); // remembered
    }

    [Fact]
    public async Task A_shutdown_during_the_publish_still_escapes_rather_than_failing_the_run()
    {
        // The worker stopping is not this run failing: failing it would refund a run whose draft PR may
        // be open by the time the request lands. The cancellation is delivered while the PR request waits.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        var pullRequest = github.Holds[$"POST /repos/{Repo}/pulls"] = new StubHttpHandler.Hold();
        using var shutdown = new CancellationTokenSource();

        var run = await gateway.StartAsync(Spec());
        var poll = gateway.PollAsync(run.RunId, shutdown.Token);
        await pullRequest.Arrived.Task;
        await shutdown.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);
        Assert.True(pullRequest.AbandonedByCaller);
    }

    [Fact]
    public async Task A_poll_that_times_out_reports_still_running()
    {
        // Failing the run here would refund it and abandon a session that goes on spending vendor-side.
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Failures["GET /v1/sessions/sesn_1"] = new TaskCanceledException("timed out");

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Running, progress.Status);
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
    public async Task A_rescheduling_session_is_still_running_and_finishes_when_it_goes_idle()
    {
        // The vendor retries a transient error itself and reports "rescheduling" while it does, so the
        // session is still working. Failing the run here would refund it while the session spends on.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Sequences["GET /v1/sessions/sesn_1"] = new Queue<(HttpStatusCode, string)>([
            (HttpStatusCode.OK, """{"id":"sesn_1","status":"rescheduling"}"""),
        ]);

        var run = await gateway.StartAsync(Spec());
        Assert.Equal(AgentRunStatus.Running, (await gateway.PollAsync(run.RunId)).Status);
        var progress = await gateway.PollAsync(run.RunId);

        Assert.True(progress.Status == AgentRunStatus.Succeeded, progress.Error);
        Assert.Equal("https://gh.test/acme/app/pull/9", progress.PrUrl);
    }

    [Theory]
    [InlineData("terminated", false)]
    [InlineData("a_status_this_version_does_not_know", true)]
    public async Task A_terminated_or_unknown_session_fails_the_run_with_the_vendor_status(
        string status, bool stopsTheSession)
    {
        // A status this version does not know may be a session still working and spending, and failing
        // the run does not stop it. A terminated session has nothing left to stop.
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Routes["GET /v1/sessions/sesn_1"] = (HttpStatusCode.OK, $$"""{"id":"sesn_1","status":"{{status}}"}""");
        cma.Routes["POST /v1/sessions/sesn_1/events"] = (HttpStatusCode.OK, "{}");

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, progress.Status);
        Assert.Contains(status, progress.Error);
        Assert.Equal(stopsTheSession, cma.Requests.Any(r => r.Key == "POST /v1/sessions/sesn_1/events"));
        Assert.Equal(stopsTheSession, cma.Requests.Any(r => r.Key == "DELETE /v1/sessions/sesn_1"));
    }

    [Theory]
    [InlineData("budget_reached", FixFailureReason.NoUsableFix)]
    [InlineData("refusal", FixFailureReason.ModelRefused)]
    [InlineData("retries_exhausted", FixFailureReason.ModelUnavailable)]
    [InlineData("requires_action", FixFailureReason.AgentBlocked)]
    [InlineData("a_reason_this_version_does_not_know", FixFailureReason.Unknown)]
    public async Task An_idle_session_that_did_not_finish_its_turn_opens_nothing_and_says_why(
        string stopReason, FixFailureReason reason)
    {
        // A session that reaches its budget goes idle rather than terminating, with a usable-looking plan as
        // its last message. That plan is unfinished work, so no draft PR may come of it, and the session is
        // deleted, since its sandbox holds the cloned repository.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Routes[EventsRoute] =
            (HttpStatusCode.OK, FinalMessageEvents(UsablePlan, stopReason));

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, progress.Status);
        Assert.Equal(reason, progress.Reason);
        Assert.Contains(stopReason, progress.Error);
        // Only the read of the branch head the session cloned, at start; no other git or pull request call.
        Assert.DoesNotContain(github.Requests, r => r.Key != $"GET /repos/{Repo}/git/ref/heads/main"
            && (r.Key.Contains("/git/") || r.Key.Contains("/pulls")));
        Assert.Contains(cma.Requests, r => r.Key == "DELETE /v1/sessions/sesn_1");
    }

    [Fact]
    public async Task An_idle_session_whose_idle_event_is_not_listed_yet_is_read_again_on_the_next_poll()
    {
        // Nothing says why the session stopped yet, so it is not known to have finished. Failing here would
        // throw away a finished run, and finalizing would trust an answer that may be unfinished.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        var withoutIdle = JsonSerializer.Serialize(new
        {
            data = new object[] { new { type = "agent.message", content = new[] { new { type = "text", text = UsablePlan } } } },
        });
        cma.Sequences[EventsRoute] =
            new Queue<(HttpStatusCode, string)>([(HttpStatusCode.OK, withoutIdle)]);

        var run = await gateway.StartAsync(Spec());
        var first = await gateway.PollAsync(run.RunId);
        Assert.Equal(AgentRunStatus.Running, first.Status);
        Assert.DoesNotContain(github.Requests, r => r.Key.Contains("/pulls"));
        Assert.DoesNotContain(cma.Requests, r => r.Key.StartsWith("DELETE ", StringComparison.Ordinal));

        var second = await gateway.PollAsync(run.RunId);
        Assert.True(second.Status == AgentRunStatus.Succeeded, second.Error);
        Assert.Equal("https://gh.test/acme/app/pull/9", second.PrUrl);
    }

    [Fact]
    public async Task The_latest_idle_event_decides_not_an_earlier_one()
    {
        // A session that paused for a confirmation and then finished has both idle events, newest first.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Routes[EventsRoute] = (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                new { type = "session.status_idle", stop_reason = new { type = "end_turn" } },
                new { type = "agent.message", content = new[] { new { type = "text", text = UsablePlan } } },
                new { type = "session.status_idle", stop_reason = new { type = "requires_action" } },
            },
        }));

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.True(progress.Status == AgentRunStatus.Succeeded, progress.Error);
    }

    [Fact]
    public async Task An_unusable_final_message_fails_the_run_and_opens_nothing()
    {
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Routes["GET /v1/sessions/sesn_1"] = (HttpStatusCode.OK, IdleSession());
        cma.Routes[EventsRoute] = (HttpStatusCode.OK, FinalMessageEvents(
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
        cma.Routes[EventsRoute] = (HttpStatusCode.OK, FinalMessageEvents(
            """{"summary":"evil","files":[{"path":"../../etc/passwd","contents":"x"}]}"""));

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, progress.Status);
        // The offending path, not either module's wording: the rule moved from this gateway into the
        // shared RepoPaths and its message came with it, and a test pinned to a phrase would have read
        // as a regression when nothing about the behaviour changed.
        Assert.Contains("../../etc/passwd", progress.Error);
        // Only the read of the branch head the session cloned, at start; no other git or pull request call.
        Assert.DoesNotContain(github.Requests, r => r.Key != $"GET /repos/{Repo}/git/ref/heads/main"
            && (r.Key.Contains("/git/") || r.Key.Contains("/pulls")));
    }

    [Fact]
    public async Task An_idle_event_whose_reason_cannot_be_read_fails_the_run_now()
    {
        // Reading again would find the same event, so waiting for it would only spend the poll budget.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Routes[EventsRoute] = (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                new { type = "session.status_idle" },
                new { type = "agent.message", content = new[] { new { type = "text", text = UsablePlan } } },
            },
        }));

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, progress.Status);
        Assert.Equal(FixFailureReason.Unknown, progress.Reason);
        Assert.DoesNotContain(github.Requests, r => r.Key.Contains("/pulls"));
    }

    [Fact]
    public async Task A_newest_message_with_no_text_falls_back_to_the_one_before_it()
    {
        // A message can carry only a redacted block, which has no text.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Routes[EventsRoute] = (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                new { type = "session.status_idle", stop_reason = new { type = "end_turn" } },
                new { type = "agent.message", content = new[] { new { type = "redacted_thinking" } } },
                new { type = "agent.message", content = new[] { new { type = "text", text = UsablePlan } } },
            },
        }));

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.True(progress.Status == AgentRunStatus.Succeeded, progress.Error);
    }

    [Fact]
    public async Task A_turn_ended_by_an_interrupt_opens_nothing_although_it_reports_end_turn()
    {
        // The vendor reports end_turn for an interrupted turn too; the interrupt event is what tells them apart.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Routes[EventsRoute] = (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                new { type = "session.status_idle", stop_reason = new { type = "end_turn" } },
                new { type = "user.interrupt" },
                new { type = "agent.message", content = new[] { new { type = "text", text = UsablePlan } } },
            },
        }));

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, progress.Status);
        Assert.Equal(FixFailureReason.StoppedExternally, progress.Reason);
        Assert.DoesNotContain(github.Requests, r => r.Key.Contains("/pulls"));
    }

    [Fact]
    public async Task An_interrupt_in_an_earlier_turn_does_not_fail_a_turn_that_finished()
    {
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Routes[EventsRoute] = (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                new { type = "session.status_idle", stop_reason = new { type = "end_turn" } },
                new { type = "agent.message", content = new[] { new { type = "text", text = UsablePlan } } },
                new { type = "session.status_idle", stop_reason = new { type = "end_turn" } },
                new { type = "user.interrupt" },
            },
        }));

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.True(progress.Status == AgentRunStatus.Succeeded, progress.Error);
    }

    [Fact]
    public async Task A_server_error_on_the_event_read_fails_the_run_at_once_with_the_vendors_answer()
    {
        // Retrying it would report a vendor outage as a timeout once the poll budget ran out.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Routes[EventsRoute] =
            (HttpStatusCode.BadGateway, """{"type":"error","error":{"type":"api_error","message":"bad gateway"}}""");

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, progress.Status);
        Assert.Equal(FixFailureReason.ModelUnavailable, progress.Reason);
    }

    [Fact]
    public async Task An_earlier_end_turn_does_not_finish_a_session_that_stopped_again()
    {
        // The session finished a turn, went on, and then stopped at its budget. Its latest stop is the one
        // that holds, so no draft PR may come from it.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Routes[EventsRoute] = (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = new object[]
            {
                new { type = "session.status_idle", stop_reason = new { type = "budget_reached" } },
                new { type = "agent.message", content = new[] { new { type = "text", text = UsablePlan } } },
                new { type = "session.status_idle", stop_reason = new { type = "end_turn" } },
                new { type = "agent.message", content = new[] { new { type = "text", text = UsablePlan } } },
            },
        }));

        var run = await gateway.StartAsync(Spec());
        var progress = await gateway.PollAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, progress.Status);
        Assert.DoesNotContain(github.Requests, r => r.Key.Contains("/pulls"));
    }

    [Fact]
    public async Task A_throttled_event_read_is_read_again_rather_than_failing_a_finished_run()
    {
        // The events are read on every idle poll, so a throttled read must not throw away a run that has
        // finished. The poll after it opens the draft PR.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Sequences[EventsRoute] = new Queue<(HttpStatusCode, string)>([
            (HttpStatusCode.TooManyRequests, """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}"""),
        ]);

        var run = await gateway.StartAsync(Spec());
        Assert.Equal(AgentRunStatus.Running, (await gateway.PollAsync(run.RunId)).Status);
        var progress = await gateway.PollAsync(run.RunId);

        Assert.True(progress.Status == AgentRunStatus.Succeeded, progress.Error);
    }

    [Fact]
    public async Task Installation_zero_is_refused_up_front()
    {
        var (gateway, _, _) = Create();
        var error = await Assert.ThrowsAsync<FixFailedException>(
            () => gateway.StartAsync(Spec() with { InstallationId = 0 }));
        Assert.Equal(FixFailureReason.SourceHostAccess, error.Reason);
    }

    [Fact]
    public async Task An_existing_unarchived_agent_is_reused_not_recreated()
    {
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Routes["GET /v1/agents?limit=100&include_archived=true"] = (HttpStatusCode.OK,
            """{"data":[{"id":"agent_old","name":"condux-conductor-claude-opus-4-8","archived_at":null}]}""");

        await gateway.StartAsync(Spec());

        Assert.DoesNotContain(cma.Requests, r => r.Key == "POST /v1/agents");
        AssertSessionRunsAs(cma, "agent_old", setsTools: true);
    }

    [Fact]
    public async Task A_new_agent_is_created_with_the_prompt_the_session_runs_with()
    {
        var (gateway, cma, github) = Create();
        StubStart(cma, github);

        await gateway.StartAsync(Spec());

        var agent = JsonDocument.Parse(cma.Requests.Single(r => r.Key == "POST /v1/agents").Body).RootElement;
        Assert.Equal("condux-conductor-claude-opus-4-8", agent.GetProperty("name").GetString());
        Assert.Equal(ManagedAgentsProvisioner.SystemPrompt, agent.GetProperty("system").GetString());
        AssertToolsetWithReadAndWebOff(agent.GetProperty("tools"));
        AssertSessionRunsAs(cma, "agent_1", setsTools: true);
    }

    [Fact]
    public async Task A_pinned_agent_runs_the_code_prompt_and_keeps_its_own_tools()
    {
        var (gateway, cma, github) = Create(pinnedAgentId: "agent_pinned");
        StubStart(cma, github);

        await gateway.StartAsync(Spec());

        Assert.DoesNotContain(cma.Requests, r => r.Key.Contains("/v1/agents", StringComparison.Ordinal));
        AssertSessionRunsAs(cma, "agent_pinned", setsTools: false);
    }

    [Fact]
    public async Task A_new_environment_is_created_with_no_egress()
    {
        var (gateway, cma, github) = Create();
        StubStart(cma, github);

        await gateway.StartAsync(Spec());

        var create = JsonDocument.Parse(cma.Requests.Single(r => r.Key == "POST /v1/environments").Body).RootElement;
        Assert.Equal("condux-conductor", create.GetProperty("name").GetString());
        AssertNoEgress(create.GetProperty("config"));
        var session = JsonDocument.Parse(cma.Requests.Single(r => r.Key == "POST /v1/sessions").Body).RootElement;
        Assert.Equal("env_1", session.GetProperty("environment_id").GetString());
    }

    [Fact]
    public async Task An_environment_found_by_name_is_updated_to_no_egress_before_it_is_used()
    {
        // An older build created it with no networking, which the vendor reads as unrestricted, and a
        // session cannot override an environment's networking.
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Routes[EnvironmentsRoute] = (HttpStatusCode.OK,
            """{"data":[{"id":"env_old","name":"condux-conductor","archived_at":null}]}""");
        cma.Routes["POST /v1/environments/env_old"] = (HttpStatusCode.OK, EnvironmentWithNoEgress("env_old"));

        await gateway.StartAsync(Spec());

        Assert.DoesNotContain(cma.Requests, r => r.Key == "POST /v1/environments");
        var update = JsonDocument.Parse(cma.Requests.Single(r => r.Key == "POST /v1/environments/env_old").Body).RootElement;
        AssertNoEgress(update.GetProperty("config"));
        var keys = cma.Requests.Select(r => r.Key).ToList();
        Assert.True(keys.IndexOf("POST /v1/environments/env_old") < keys.IndexOf("POST /v1/sessions"));
        var session = JsonDocument.Parse(cma.Requests.Single(r => r.Key == "POST /v1/sessions").Body).RootElement;
        Assert.Equal("env_old", session.GetProperty("environment_id").GetString());
    }

    [Fact]
    public async Task An_environment_on_a_later_page_is_found_and_not_created_again()
    {
        // On a key with more environments than one page holds, ours can sit past the first page. Reading
        // only that page created a new environment on every start.
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Routes[EnvironmentsRoute] = (HttpStatusCode.OK,
            """{"data":[{"id":"env_other","name":"someone-else","archived_at":null}],"next_page":"p2+/="}""");
        // The cursor is opaque, so it goes back escaped: a + read raw in a query string is a space.
        cma.Routes[EnvironmentsRoute + "&page=p2%2B%2F%3D"] = (HttpStatusCode.OK,
            """{"data":[{"id":"env_old","name":"condux-conductor","archived_at":null}],"next_page":null}""");
        cma.Routes["POST /v1/environments/env_old"] = (HttpStatusCode.OK, EnvironmentWithNoEgress("env_old"));

        await gateway.StartAsync(Spec());

        Assert.DoesNotContain(cma.Requests, r => r.Key == "POST /v1/environments");
        var session = JsonDocument.Parse(cma.Requests.Single(r => r.Key == "POST /v1/sessions").Body).RootElement;
        Assert.Equal("env_old", session.GetProperty("environment_id").GetString());
    }

    [Fact]
    public async Task A_list_cursor_that_never_ends_stops_at_the_page_bound()
    {
        // A vendor bug that kept handing back a cursor must not hold the run in a loop of reads.
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Routes[EnvironmentsRoute] = (HttpStatusCode.OK, """{"data":[],"next_page":"again"}""");
        cma.Routes[EnvironmentsRoute + "&page=again"] = (HttpStatusCode.OK, """{"data":[],"next_page":"again"}""");

        await gateway.StartAsync(Spec());

        Assert.Equal(50, cma.Requests.Count(r => r.Key.StartsWith(EnvironmentsRoute, StringComparison.Ordinal)));
        Assert.Contains(cma.Requests, r => r.Key == "POST /v1/environments");
    }

    [Fact]
    public async Task An_environment_that_cannot_be_updated_starts_no_session()
    {
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Routes[EnvironmentsRoute] = (HttpStatusCode.OK,
            """{"data":[{"id":"env_old","name":"condux-conductor","archived_at":null}]}""");
        cma.Routes["POST /v1/environments/env_old"] = (HttpStatusCode.InternalServerError, """{"error":"boom"}""");

        await Assert.ThrowsAnyAsync<Exception>(() => gateway.StartAsync(Spec()));

        Assert.DoesNotContain(cma.Requests, r => r.Key == "POST /v1/sessions");
    }

    [Theory]
    [InlineData("""{"type":"unrestricted"}""")]
    [InlineData("""{"type":"unrestricted","allowed_hosts":[],"allow_package_managers":false,"allow_mcp_servers":false}""")]
    [InlineData("""{"type":"limited","allowed_hosts":["github.com"],"allow_package_managers":false,"allow_mcp_servers":false}""")]
    [InlineData("""{"type":"limited","allowed_hosts":[],"allow_package_managers":true,"allow_mcp_servers":false}""")]
    [InlineData("""{"type":"limited","allowed_hosts":[],"allow_package_managers":false,"allow_mcp_servers":true}""")]
    [InlineData("null")]
    public async Task An_environment_whose_stored_networking_allows_a_host_starts_no_session(string stored)
    {
        // The vendor answers 200 but did not store the policy, as it would if it ignored a field.
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Routes[EnvironmentsRoute] = (HttpStatusCode.OK,
            """{"data":[{"id":"env_old","name":"condux-conductor","archived_at":null}]}""");
        cma.Routes["POST /v1/environments/env_old"] = (HttpStatusCode.OK, EnvironmentWithNoEgress("env_old", stored));

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.StartAsync(Spec()));

        Assert.DoesNotContain(cma.Requests, r => r.Key == "POST /v1/sessions");
    }

    [Fact]
    public async Task A_pinned_environment_is_used_as_its_operator_configured_it()
    {
        var (gateway, cma, github) = Create(pinnedEnvironmentId: "env_pinned");
        StubStart(cma, github);

        await gateway.StartAsync(Spec());

        Assert.DoesNotContain(cma.Requests, r => r.Key.Contains("/v1/environments", StringComparison.Ordinal));
        var session = JsonDocument.Parse(cma.Requests.Single(r => r.Key == "POST /v1/sessions").Body).RootElement;
        Assert.Equal("env_pinned", session.GetProperty("environment_id").GetString());
    }

    [Fact]
    public async Task Stopping_a_running_session_interrupts_it_and_no_pull_request_follows()
    {
        // Still working when polling ends, so its answer is not finished. Once the run is stopped nothing may
        // turn a later answer into a PR, and the session is interrupted, which is the only way to stop its
        // spend: a running session cannot be deleted.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Routes["GET /v1/sessions/sesn_1"] = (HttpStatusCode.OK, """{"id":"sesn_1","status":"running"}""");
        cma.Routes["POST /v1/sessions/sesn_1/events"] = (HttpStatusCode.OK, "{}");
        var run = await gateway.StartAsync(Spec());

        var stopped = await gateway.CancelAsync(run.RunId, publishFinished: true);
        var polled = await gateway.PollAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, stopped.Status);
        Assert.Equal(AgentRunStatus.Failed, polled.Status);
        using var sent = JsonDocument.Parse(cma.Requests.Single(r => r.Key == "POST /v1/sessions/sesn_1/events").Body);
        Assert.Equal("user.interrupt", sent.RootElement.GetProperty("events")[0].GetProperty("type").GetString());
        Assert.Contains(cma.Requests, r => r.Key == "DELETE /v1/sessions/sesn_1");
        Assert.DoesNotContain(github.Requests, r => r.Key == $"POST /repos/{Repo}/pulls");
    }

    [Fact]
    public async Task Stopping_a_session_that_already_finished_its_turn_opens_its_pull_request()
    {
        // The session went idle with a usable answer between the last poll and the stop. That answer is
        // paid for, and a success wins on every path, so the stop finalizes it rather than discard it.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        var run = await gateway.StartAsync(Spec());

        var stopped = await gateway.CancelAsync(run.RunId, publishFinished: true);

        Assert.True(stopped.Status == AgentRunStatus.Succeeded, stopped.Error);
        Assert.Equal("https://gh.test/acme/app/pull/9", stopped.PrUrl);
        Assert.DoesNotContain(cma.Requests, r => r.Key == "POST /v1/sessions/sesn_1/events");
        // Remembered, so a poll after the stop reports the same PR rather than opening another.
        Assert.Equal(stopped.PrUrl, (await gateway.PollAsync(run.RunId)).PrUrl);
        Assert.Single(github.Requests, r => r.Key == $"POST /repos/{Repo}/pulls");
    }

    [Fact]
    public async Task A_stop_that_may_not_publish_discards_a_finished_answer_rather_than_open_a_pull_request()
    {
        // A shutdown's stop: source-host writes cut off part way would leave a branch or a PR no run
        // records, so the session is stopped and its answer given up, as before.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Routes["POST /v1/sessions/sesn_1/events"] = (HttpStatusCode.OK, "{}");
        var run = await gateway.StartAsync(Spec());

        var stopped = await gateway.CancelAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, stopped.Status);
        Assert.Contains(cma.Requests, r => r.Key == "POST /v1/sessions/sesn_1/events");
        // Only the read of the branch head the session cloned, at start; no other git or pull request call.
        Assert.DoesNotContain(github.Requests, r => r.Key != $"GET /repos/{Repo}/git/ref/heads/main"
            && (r.Key.Contains("/git/") || r.Key.Contains("/pulls")));
    }

    [Fact]
    public async Task Stopping_a_session_idle_for_another_reason_fails_with_that_reason_and_opens_nothing()
    {
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Routes[EventsRoute] = (HttpStatusCode.OK, FinalMessageEvents(UsablePlan, "budget_reached"));
        var run = await gateway.StartAsync(Spec());

        var stopped = await gateway.CancelAsync(run.RunId, publishFinished: true);

        Assert.Equal(AgentRunStatus.Failed, stopped.Status);
        Assert.Equal(FixFailureReason.NoUsableFix, stopped.Reason);
        // Remembered: a poll after the stop must not finalize the session a second time.
        Assert.Equal(FixFailureReason.NoUsableFix, (await gateway.PollAsync(run.RunId)).Reason);
        Assert.DoesNotContain(github.Requests, r => r.Key.Contains("/pulls"));
        Assert.Contains(cma.Requests, r => r.Key == "DELETE /v1/sessions/sesn_1");
    }

    [Fact]
    public async Task Stopping_an_idle_session_that_has_not_said_why_stops_it_as_before()
    {
        // Nothing says the turn finished, and polling is over, so there is no next read to wait for.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Routes[EventsRoute] = (HttpStatusCode.OK, """{"data":[]}""");
        cma.Routes["POST /v1/sessions/sesn_1/events"] = (HttpStatusCode.OK, "{}");
        var run = await gateway.StartAsync(Spec());

        var stopped = await gateway.CancelAsync(run.RunId, publishFinished: true);

        Assert.Equal(AgentRunStatus.Failed, stopped.Status);
        Assert.Contains(cma.Requests, r => r.Key == "POST /v1/sessions/sesn_1/events");
        Assert.Equal(AgentRunStatus.Failed, (await gateway.PollAsync(run.RunId)).Status);
    }

    [Fact]
    public async Task Stopping_a_finished_session_whose_events_read_is_throttled_stops_it_as_before()
    {
        // Polling is over, so there is no next read: the answer cannot be confirmed finished.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Routes[EventsRoute] =
            (HttpStatusCode.TooManyRequests, """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""");
        cma.Routes["POST /v1/sessions/sesn_1/events"] = (HttpStatusCode.OK, "{}");
        var run = await gateway.StartAsync(Spec());

        var stopped = await gateway.CancelAsync(run.RunId, publishFinished: true);

        Assert.Equal(FixFailureReason.TimedOut, stopped.Reason);
        Assert.Contains(cma.Requests, r => r.Key == "POST /v1/sessions/sesn_1/events");
        Assert.DoesNotContain(github.Requests, r => r.Key.Contains("/pulls"));
    }

    [Fact]
    public async Task Stopping_a_finished_session_whose_events_read_is_refused_fails_as_a_poll_would()
    {
        // A refusal is not retried by a poll either: the run fails with the vendor's answer, and the idle
        // session is deleted rather than interrupted.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        cma.Routes[EventsRoute] =
            (HttpStatusCode.BadGateway, """{"type":"error","error":{"type":"api_error","message":"bad gateway"}}""");
        var run = await gateway.StartAsync(Spec());

        var stopped = await gateway.CancelAsync(run.RunId, publishFinished: true);

        Assert.Equal(FixFailureReason.ModelUnavailable, stopped.Reason);
        Assert.Contains(cma.Requests, r => r.Key == "DELETE /v1/sessions/sesn_1");
        Assert.DoesNotContain(cma.Requests, r => r.Key == "POST /v1/sessions/sesn_1/events");
    }

    [Fact]
    public async Task Stopping_a_session_whose_status_cannot_be_read_stops_it_as_before()
    {
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Failures["GET /v1/sessions/sesn_1"] = new HttpRequestException("connection reset");
        cma.Routes["POST /v1/sessions/sesn_1/events"] = (HttpStatusCode.OK, "{}");
        var run = await gateway.StartAsync(Spec());

        var stopped = await gateway.CancelAsync(run.RunId, publishFinished: true);

        Assert.Equal(AgentRunStatus.Failed, stopped.Status);
        Assert.Equal(FixFailureReason.TimedOut, stopped.Reason);
        Assert.Contains(cma.Requests, r => r.Key == "POST /v1/sessions/sesn_1/events");
    }

    [Fact]
    public async Task Stopping_a_finished_session_whose_publish_fails_deletes_it_and_reports_the_failure()
    {
        // Polling is over, so the failure is the run's end, and the idle session can be deleted now.
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        github.Routes[$"POST /repos/{Repo}/pulls"] = (HttpStatusCode.UnprocessableEntity, """{"message":"Validation Failed"}""");
        var run = await gateway.StartAsync(Spec());

        var stopped = await gateway.CancelAsync(run.RunId, publishFinished: true);

        Assert.Equal(AgentRunStatus.Failed, stopped.Status);
        Assert.Contains(cma.Requests, r => r.Key == "DELETE /v1/sessions/sesn_1");
        Assert.DoesNotContain(cma.Requests, r => r.Key == "POST /v1/sessions/sesn_1/events");
        // Remembered: a poll after the stop must not try the publish again.
        Assert.Equal(AgentRunStatus.Failed, (await gateway.PollAsync(run.RunId)).Status);
        Assert.Single(github.Requests, r => r.Key == $"POST /repos/{Repo}/pulls");
    }

    [Fact]
    public async Task A_stop_the_vendor_refuses_still_ends_the_run_for_condux()
    {
        var (gateway, cma, github) = Create();
        StubStart(cma, github);
        cma.Routes["POST /v1/sessions/sesn_1/events"] = (HttpStatusCode.InternalServerError, "{}");
        var run = await gateway.StartAsync(Spec());

        var stopped = await gateway.CancelAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Failed, stopped.Status);
        // A refused interrupt does not skip the delete, which succeeds on a session that already stopped.
        Assert.Contains(cma.Requests, r => r.Key == "DELETE /v1/sessions/sesn_1");
    }

    [Fact]
    public async Task Stopping_a_run_this_gateway_never_started_sends_nothing()
    {
        var (gateway, cma, _) = Create();

        var stopped = await gateway.CancelAsync("sesn_someone_else");

        Assert.Equal(AgentRunStatus.Failed, stopped.Status);
        Assert.Empty(cma.Requests);
    }

    [Fact]
    public async Task Stopping_a_run_that_already_opened_its_pull_request_reports_that_pull_request()
    {
        var (gateway, cma, github) = Create();
        StubFinishedRun(cma, github);
        var run = await gateway.StartAsync(Spec());
        await gateway.PollAsync(run.RunId);

        var stopped = await gateway.CancelAsync(run.RunId);

        Assert.Equal(AgentRunStatus.Succeeded, stopped.Status);
        Assert.Equal("https://gh.test/acme/app/pull/9", stopped.PrUrl);
        Assert.DoesNotContain(cma.Requests, r => r.Key == "POST /v1/sessions/sesn_1/events");
    }

    [Fact]
    public async Task Unknown_run_ids_throw()
    {
        var (gateway, _, _) = Create();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => gateway.PollAsync("sesn_nope"));
    }
}
