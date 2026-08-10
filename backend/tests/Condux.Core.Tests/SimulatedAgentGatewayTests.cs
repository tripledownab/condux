using Condux.Core.FixEngine;
using Xunit;

namespace Condux.Core.Tests;

public class SimulatedAgentGatewayTests
{
    private static readonly AgentRunSpec Spec = new("42", "acme/api", "main", "Fix issue 42.", "claude-opus-4-8");

    [Fact]
    public async Task Reports_running_then_succeeds_with_a_non_routable_pr()
    {
        var gateway = new SimulatedAgentGateway(pollsWhileRunning: 2);
        var run = await gateway.StartAsync(Spec);

        Assert.Equal(AgentRunStatus.Running, (await gateway.PollAsync(run.RunId)).Status);
        Assert.Equal(AgentRunStatus.Running, (await gateway.PollAsync(run.RunId)).Status);

        var done = await gateway.PollAsync(run.RunId);
        Assert.Equal(AgentRunStatus.Succeeded, done.Status);
        Assert.Equal("condux/fix-42", done.Branch);
        Assert.Contains("example.invalid", done.PrUrl); // never a routable, real-looking PR
        Assert.Equal("simulated-agent", gateway.BackendName);
    }

    [Fact]
    public async Task Polling_an_unknown_run_throws()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => new SimulatedAgentGateway().PollAsync("nope"));
    }
}
