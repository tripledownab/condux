using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Condux.Agent;
using Condux.Core.FixEngine;
using Condux.GitHub;
using Xunit;

namespace Condux.Conductor.Tests;

/// <summary>
/// The whole v1 fix run, offline: mint token → fetch scoped file → Claude plan → branch → commit →
/// draft PR, with both the GitHub API and the Anthropic API stubbed. CI never touches a real service.
/// </summary>
public class AnthropicAgentGatewayTests
{
    private const string Repo = "acme/api";

    private static readonly AgentRunSpec Spec =
        new("42", Repo, "main", "Error: TypeError ...", "claude-opus-4-8")
        {
            ScopedPaths = ["src/cart.js"],
            InstallationId = 7,
        };


    /// <summary>A provider that cannot drive a tool loop, to prove such an org still gets fixes.</summary>
    private sealed class ToollessClient(IModelClient inner) : IModelClient
    {
        public string Provider => inner.Provider;

        public bool SupportsTools => false;

        public Task<ModelOutput> CreateAsync(
            string model, string system, string user, string apiKey, string baseUrl,
            CancellationToken ct = default) =>
            inner.CreateAsync(model, system, user, apiKey, baseUrl, ct);
    }

    private static (AnthropicAgentGateway Gateway, StubHttpHandler GitHub, StubHttpHandler Anthropic) Create(
        bool agentic = false, bool supportsTools = true)
    {
        var github = new StubHttpHandler();
        var anthropic = new StubHttpHandler();
        var options = new GitHubAppOptions("Iv1.test", RSA.Create(2048).ExportRSAPrivateKeyPem(), "")
        {
            ApiBaseUrl = "https://gh.test",
        };
        IModelClient anthropicClient = new AnthropicMessagesClient(
            new HttpClient(anthropic),
            new AnthropicOptions("sk-ant-test") { BaseUrl = "https://anthropic.test" });
        var gateway = new AnthropicAgentGateway(
            [supportsTools ? anthropicClient : new ToollessClient(anthropicClient)],
            new GitHubInstallationTokens(new HttpClient(github), options, () => DateTimeOffset.UtcNow),
            new GitHubRepoClient(new HttpClient(github), "https://gh.test"),
            // No secret store configured, so every run uses the platform default key.
            new ModelKeyResolver("sk-ant-test", configs: null, box: null),
            new HttpClient(anthropic),
            agentic)
        {
            AgentApiBaseUrl = "https://anthropic.test",
        };
        return (gateway, github, anthropic);
    }

    private static async Task<AgentRunProgress> PollToCompletionAsync(AnthropicAgentGateway gateway, string runId)
    {
        // The run is a real background task; poll until it leaves Running (bounded so a hang fails fast).
        for (var i = 0; i < 500; i++)
        {
            var progress = await gateway.PollAsync(runId);
            if (progress.Status != AgentRunStatus.Running)
            {
                return progress;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("The stubbed run never finished.");
    }

    // The agentic path (ADR-0033): the model drives tools over several turns instead of returning one
    // plan, and the gateway commits whatever the workspace staged. Same GitHub stubs, same draft PR.
    [Fact]
    public async Task Agentic_mode_drives_tools_over_several_turns_and_opens_a_draft_pr()
    {
        var (gateway, github, anthropic) = Create(agentic: true);
        var cartJs = Convert.ToBase64String(Encoding.UTF8.GetBytes("broken()\n"));
        github.Routes["POST /app/installations/7/access_tokens"] =
            (HttpStatusCode.Created,
             $$"""{"token":"ghs_x","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1):O}}"}""");
        github.Routes[$"GET /repos/{Repo}/contents/src/cart.js?ref=main"] =
            (HttpStatusCode.OK, $$"""{"content":"{{cartJs}}","sha":"blob-sha"}""");
        github.Routes[$"GET /repos/{Repo}/git/ref/heads/main"] =
            (HttpStatusCode.OK, """{"object":{"sha":"base-sha"}}""");
        github.Routes[$"POST /repos/{Repo}/git/refs"] = (HttpStatusCode.Created, "{}");
        github.Routes[$"PUT /repos/{Repo}/contents/src/cart.js"] = (HttpStatusCode.OK, "{}");
        github.Routes[$"POST /repos/{Repo}/pulls"] =
            (HttpStatusCode.Created, """{"html_url":"https://gh.test/acme/api/pull/11"}""");

        anthropic.Sequences["POST /v1/messages"] = new Queue<(HttpStatusCode, string)>(
        [
            (HttpStatusCode.OK, ToolUse("t1", AgentToolNames.ReadFile, new { path = "src/cart.js" }, 500, 40)),
            (HttpStatusCode.OK, ToolUse("t2", AgentToolNames.WriteFile, new { path = "src/cart.js", contents = "fixed()\n" }, 600, 60)),
            (HttpStatusCode.OK, ToolUse("t3", AgentToolNames.Finish, new { summary = "Guard the empty cart." }, 300, 20)),
        ]);

        var run = await gateway.StartAsync(Spec);
        var done = await PollToCompletionAsync(gateway, run.RunId);

        Assert.Equal(AgentRunStatus.Succeeded, done.Status);
        Assert.Equal("https://gh.test/acme/api/pull/11", done.PrUrl);
        Assert.Equal("Guard the empty cart.", done.Summary);
        // Usage accumulates across every turn, not just the last one.
        Assert.Equal(1400, done.InputTokens);
        Assert.Equal(120, done.OutputTokens);

        var turns = anthropic.Requests.Where(r => r.Key == "POST /v1/messages").ToList();
        Assert.Equal(3, turns.Count);
        // The tools were offered, and the file the agent read came back as a tool_result keyed to its call.
        Assert.Contains(AgentToolNames.WriteFile, turns[0].Body, StringComparison.Ordinal);
        Assert.Contains("tool_result", turns[1].Body, StringComparison.Ordinal);
        Assert.Contains("broken()", turns[1].Body, StringComparison.Ordinal);
        // What the agent staged is what got committed.
        using var put = JsonDocument.Parse(
            github.Requests.Single(r => r.Key == $"PUT /repos/{Repo}/contents/src/cart.js").Body);
        Assert.Equal(
            "fixed()\n",
            Encoding.UTF8.GetString(Convert.FromBase64String(put.RootElement.GetProperty("content").GetString()!)));
    }

    // Either the frames never resolved (nothing scoped) or the mapping points somewhere wrong (scoped but
    // missing). Both reach the model with nothing to show it, so both must stop.
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task A_run_that_could_read_no_file_fails_without_calling_the_model_or_opening_a_pr(
        bool agentic, bool scoped)
    {
        var (gateway, github, anthropic) = Create(agentic);
        github.Routes["POST /app/installations/7/access_tokens"] =
            (HttpStatusCode.Created,
             $$"""{"token":"ghs_x","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1):O}}"}""");
        // The contents route is left unstubbed, so a scoped path 404s.

        var run = await gateway.StartAsync(scoped ? Spec : Spec with { ScopedPaths = [] });
        var done = await PollToCompletionAsync(gateway, run.RunId);

        Assert.Equal(AgentRunStatus.Failed, done.Status);
        // The three things that used to happen anyway, and are the real cost of guessing.
        Assert.DoesNotContain(anthropic.Requests, r => r.Key == "POST /v1/messages");
        Assert.DoesNotContain(github.Requests, r => r.Key == $"POST /repos/{Repo}/git/refs");
        Assert.DoesNotContain(github.Requests, r => r.Key == $"POST /repos/{Repo}/pulls");
        Assert.Contains("code mapping", done.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToolUse(string id, string name, object input, long inputTokens, long outputTokens) =>
        JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "tool_use", id, name, input } },
            stop_reason = "tool_use",
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
        });

    [Fact]
    public async Task Runs_the_full_flow_and_opens_a_draft_pr()
    {
        var (gateway, github, anthropic) = Create();
        var cartJs = Convert.ToBase64String(Encoding.UTF8.GetBytes("broken()\n"));
        github.Routes["POST /app/installations/7/access_tokens"] =
            (HttpStatusCode.Created,
             $$"""{"token":"ghs_x","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1):O}}"}""");
        github.Routes[$"GET /repos/{Repo}/contents/src/cart.js?ref=main"] =
            (HttpStatusCode.OK, $$"""{"content":"{{cartJs}}","sha":"blob-sha"}""");
        github.Routes[$"GET /repos/{Repo}/git/ref/heads/main"] =
            (HttpStatusCode.OK, """{"object":{"sha":"base-sha"}}""");
        github.Routes[$"POST /repos/{Repo}/git/refs"] = (HttpStatusCode.Created, "{}");
        github.Routes[$"PUT /repos/{Repo}/contents/src/cart.js"] = (HttpStatusCode.OK, "{}");
        github.Routes[$"POST /repos/{Repo}/pulls"] =
            (HttpStatusCode.Created, """{"html_url":"https://gh.test/acme/api/pull/9"}""");

        var planJson = JsonSerializer.Serialize(new
        {
            summary = "Guard the empty cart.",
            files = new[] { new { path = "src/cart.js", contents = "fixed()\n" } },
        });
        anthropic.Routes["POST /v1/messages"] = (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = planJson } },
            stop_reason = "end_turn",
            usage = new { input_tokens = 900, output_tokens = 210 },
        }));

        var run = await gateway.StartAsync(Spec);
        var done = await PollToCompletionAsync(gateway, run.RunId);

        Assert.Equal(AgentRunStatus.Succeeded, done.Status);
        Assert.Equal("https://gh.test/acme/api/pull/9", done.PrUrl);
        Assert.StartsWith("condux/fix-42-", done.Branch);
        Assert.Equal("Guard the empty cart.", done.Summary);
        Assert.Equal(900, done.InputTokens); // usage rides the progress into the audit
        Assert.Equal(210, done.OutputTokens);

        // The model saw the scoped file's real contents, and the commit reused its blob sha.
        var modelRequest = anthropic.Requests.Single(r => r.Key == "POST /v1/messages").Body;
        Assert.Contains("broken()", modelRequest);
        using var put = JsonDocument.Parse(
            github.Requests.Single(r => r.Key == $"PUT /repos/{Repo}/contents/src/cart.js").Body);
        Assert.Equal("blob-sha", put.RootElement.GetProperty("sha").GetString());
        // And the PR is a draft.
        using var pr = JsonDocument.Parse(
            github.Requests.Single(r => r.Key == $"POST /repos/{Repo}/pulls").Body);
        Assert.True(pr.RootElement.GetProperty("draft").GetBoolean());
    }

    [Fact]
    public async Task Fails_the_run_when_the_model_output_is_unusable()
    {
        var (gateway, github, anthropic) = Create();
        github.Routes["POST /app/installations/7/access_tokens"] =
            (HttpStatusCode.Created,
             $$"""{"token":"ghs_x","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1):O}}"}""");
        // The scoped file has to actually resolve, or the run now fails before the model is reached and
        // this asserts the wrong failure. It previously passed without this stub, which is precisely how
        // the guess-with-no-context path stayed invisible.
        github.Routes[$"GET /repos/{Repo}/contents/src/cart.js?ref=main"] =
            (HttpStatusCode.OK,
             $$"""{"content":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes("broken()\n"))}}","sha":"blob-sha"}""");
        anthropic.Routes["POST /v1/messages"] = (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = "I cannot fix this." } },
            stop_reason = "end_turn",
        }));

        var run = await gateway.StartAsync(Spec);
        var done = await PollToCompletionAsync(gateway, run.RunId);

        Assert.Equal(AgentRunStatus.Failed, done.Status);
        Assert.Contains("no JSON object", done.Error);
    }

    [Fact]
    public async Task Refuses_to_start_without_a_github_installation()
    {
        var (gateway, _, _) = Create();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.StartAsync(Spec with { InstallationId = 0 }));
    }

    [Fact]
    public async Task A_model_that_cannot_drive_tools_still_gets_a_fix_through_the_single_shot_path()
    {
        // The reason the single-shot path is kept rather than replaced: tool-calling quality varies
        // sharply, and an org on a weaker or self-hosted model must not lose the Conductor entirely.
        var (gateway, github, anthropic) = Create(agentic: true, supportsTools: false);
        var cartJs = Convert.ToBase64String(Encoding.UTF8.GetBytes("broken()\n"));
        github.Routes["POST /app/installations/7/access_tokens"] =
            (HttpStatusCode.Created,
             $$"""{"token":"ghs_x","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1):O}}"}""");
        github.Routes[$"GET /repos/{Repo}/contents/src/cart.js?ref=main"] =
            (HttpStatusCode.OK, $$"""{"content":"{{cartJs}}","sha":"blob-sha"}""");
        github.Routes[$"GET /repos/{Repo}/git/ref/heads/main"] =
            (HttpStatusCode.OK, """{"object":{"sha":"base-sha"}}""");
        github.Routes[$"POST /repos/{Repo}/git/refs"] = (HttpStatusCode.Created, "{}");
        github.Routes[$"PUT /repos/{Repo}/contents/src/cart.js"] = (HttpStatusCode.OK, "{}");
        github.Routes[$"POST /repos/{Repo}/pulls"] =
            (HttpStatusCode.Created, """{"html_url":"https://gh.test/acme/api/pull/12"}""");
        anthropic.Routes["POST /v1/messages"] = (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = """{"summary":"Guard the cart.","files":[{"path":"src/cart.js","contents":"fixed()"}]}""",
                },
            },
            usage = new { input_tokens = 400, output_tokens = 30 },
        }));

        var run = await gateway.StartAsync(Spec);
        var done = await PollToCompletionAsync(gateway, run.RunId);

        Assert.Equal(AgentRunStatus.Succeeded, done.Status);
        Assert.Equal("https://gh.test/acme/api/pull/12", done.PrUrl);
        // One call, not a loop: the agentic path was never entered despite agentic being on.
        Assert.Single(anthropic.Requests, r => r.Key == "POST /v1/messages");
    }
}
