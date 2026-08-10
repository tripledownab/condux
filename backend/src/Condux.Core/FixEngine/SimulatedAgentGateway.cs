using System.Collections.Concurrent;

namespace Condux.Core.FixEngine;

/// <summary>
/// A deterministic <see cref="IAgentGateway"/> that walks the full start → poll → succeed lifecycle without
/// calling any LLM or GitHub: a run reports RUNNING for a few polls, then SUCCEEDED with a draft-PR result on
/// a reserved non-routable domain (so it can never be mistaken for a real pull request). It exercises the
/// real orchestration + poll UX locally and in tests, and stands in until the Anthropic Managed Agents
/// gateway lands (which slots into this same seam).
/// </summary>
public sealed class SimulatedAgentGateway(int pollsWhileRunning = 2) : IAgentGateway
{
    private readonly ConcurrentDictionary<string, Run> runs = new();

    public string BackendName => "simulated-agent";

    public Task<AgentRun> StartAsync(AgentRunSpec spec, CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid().ToString("n");
        runs[runId] = new Run(spec, pollsWhileRunning);
        return Task.FromResult(new AgentRun(runId));
    }

    public Task<AgentRunProgress> PollAsync(string runId, CancellationToken cancellationToken = default)
    {
        if (!runs.TryGetValue(runId, out var run))
        {
            throw new KeyNotFoundException($"Unknown agent run '{runId}'.");
        }

        if (run.PollsRemaining > 0)
        {
            runs[runId] = run with { PollsRemaining = run.PollsRemaining - 1 };
            return Task.FromResult(AgentRunProgress.StillRunning);
        }

        var branch = $"condux/fix-{run.Spec.IssueId}";
        var prUrl = $"https://example.invalid/{run.Spec.RepoFullName}/pull/0";
        var summary =
            $"Simulated draft fix for issue {run.Spec.IssueId}: {branch} → {run.Spec.BaseBranch} "
            + "(simulated agent — no real changes).";
        return Task.FromResult(AgentRunProgress.Done(branch, prUrl, summary));
    }

    private sealed record Run(AgentRunSpec Spec, int PollsRemaining);
}
