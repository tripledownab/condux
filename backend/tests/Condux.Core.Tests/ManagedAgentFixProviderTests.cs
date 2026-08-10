using Condux.Core.FixEngine;
using Xunit;

namespace Condux.Core.Tests;

public class ManagedAgentFixProviderTests
{
    private static readonly FixRequest Request = new("42", "acme/api", "main", "Fix issue 42.", "claude-opus-4-8");

    // Zero interval + injected no-op delay so the poll loop never sleeps (tests stay deterministic and fast).
    private static readonly ManagedAgentOptions Fast = new(TimeSpan.Zero, MaxPolls: 5);
    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    [Fact]
    public async Task Polls_until_the_run_succeeds_then_returns_the_draft_pr()
    {
        var gateway = new ScriptedGateway("anthropic-managed-agents",
        [
            AgentRunProgress.StillRunning,
            AgentRunProgress.StillRunning,
            AgentRunProgress.Done("condux/fix-42", "https://github.com/acme/api/pull/7", "Fixed the NPE.")
                with { InputTokens = 900, OutputTokens = 210 },
        ]);
        var provider = new ManagedAgentFixProvider(gateway, Fast, NoDelay);

        var result = await provider.GenerateFixAsync(Request);

        Assert.Equal("anthropic-managed-agents", provider.Name); // provider is named after its backend
        Assert.Equal("condux/fix-42", result.Branch);
        Assert.Equal("https://github.com/acme/api/pull/7", result.PrUrl);
        Assert.Equal("Fixed the NPE.", result.Summary);
        Assert.Equal(900, result.InputTokens); // usage flows through for the cost audit
        Assert.Equal(210, result.OutputTokens);
        Assert.Equal(3, gateway.Polls);
    }

    [Fact]
    public async Task Throws_when_the_run_fails()
    {
        var gateway = new ScriptedGateway("x", [AgentRunProgress.Failed("rubric not met")]);
        var provider = new ManagedAgentFixProvider(gateway, Fast, NoDelay);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateFixAsync(Request));
        Assert.Contains("rubric not met", ex.Message);
    }

    [Fact]
    public async Task Throws_when_success_reports_no_pr_url()
    {
        // A success that somehow carries no PR URL is treated as a failure, not a silent empty result.
        var gateway = new ScriptedGateway("x", [new AgentRunProgress(AgentRunStatus.Succeeded, "b", "", "", null)]);
        var provider = new ManagedAgentFixProvider(gateway, Fast, NoDelay);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GenerateFixAsync(Request));
    }

    [Fact]
    public async Task Times_out_after_the_max_polls()
    {
        var gateway = new ScriptedGateway("x", [AgentRunProgress.StillRunning]); // never finishes
        var provider = new ManagedAgentFixProvider(gateway, new ManagedAgentOptions(TimeSpan.Zero, MaxPolls: 4), NoDelay);

        await Assert.ThrowsAsync<TimeoutException>(() => provider.GenerateFixAsync(Request));
        Assert.Equal(4, gateway.Polls);
    }

    // Replays a scripted sequence of progress values; once exhausted it repeats the last (so "always
    // running" is expressed by a single-element script).
    private sealed class ScriptedGateway(string backendName, params AgentRunProgress[] script) : IAgentGateway
    {
        private readonly Queue<AgentRunProgress> queue = new(script);

        public int Polls { get; private set; }

        public string BackendName => backendName;

        public Task<AgentRun> StartAsync(AgentRunSpec spec, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentRun("run-1"));

        public Task<AgentRunProgress> PollAsync(string runId, CancellationToken cancellationToken = default)
        {
            Polls++;
            return Task.FromResult(queue.Count > 1 ? queue.Dequeue() : queue.Peek());
        }
    }
}
