namespace Condux.Core.FixEngine;

/// <summary>Timing for the poll loop: how often to poll a run and how many times before giving up.</summary>
public sealed record ManagedAgentOptions(TimeSpan PollInterval, int MaxPolls)
{
    // 5s between polls, up to 120 polls (~10 minutes) before a run is declared timed out. One place to
    // change the run budget; the Conductor can override from config.
    public static ManagedAgentOptions Default { get; } = new(TimeSpan.FromSeconds(5), 120);
}

/// <summary>
/// The provider-agnostic orchestration for one fix run: start an agentic run on an <see cref="IAgentGateway"/>,
/// poll it to completion, and map the outcome to a <see cref="FixResult"/> (the draft PR). A run that fails,
/// times out, or reports success without a PR URL throws, so the <see cref="FixOrchestrator"/> records it as
/// FAILED and audits it. The poll delay is injected so tests drive the loop without ever sleeping.
/// </summary>
public sealed class ManagedAgentFixProvider : IFixProvider
{
    private readonly IAgentGateway gateway;
    private readonly ManagedAgentOptions options;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public ManagedAgentFixProvider(
        IAgentGateway gateway,
        ManagedAgentOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.gateway = gateway;
        this.options = options ?? ManagedAgentOptions.Default;
        this.delay = delay ?? Task.Delay;
    }

    public string Name => gateway.BackendName;

    public async Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
    {
        var spec = new AgentRunSpec(
            request.IssueId, request.RepoFullName, request.BaseBranch, request.Prompt, request.Model)
        {
            ScopedPaths = request.ScopedPaths,
            InstallationId = request.InstallationId,
            OrgId = request.OrgId,
        };
        var run = await gateway.StartAsync(spec, cancellationToken);

        for (var attempt = 0; attempt < options.MaxPolls; attempt++)
        {
            var progress = await gateway.PollAsync(run.RunId, cancellationToken);
            switch (progress.Status)
            {
                case AgentRunStatus.Succeeded when string.IsNullOrEmpty(progress.PrUrl):
                    throw new InvalidOperationException("Agent run reported success without a draft PR URL.");
                case AgentRunStatus.Succeeded:
                    return new FixResult(progress.Branch, progress.PrUrl, progress.Summary)
                    {
                        InputTokens = progress.InputTokens,
                        OutputTokens = progress.OutputTokens,
                    };
                case AgentRunStatus.Failed:
                    throw new InvalidOperationException($"Agent run failed: {progress.Error}");
                default:
                    await delay(options.PollInterval, cancellationToken);
                    break;
            }
        }

        throw new TimeoutException($"Agent run did not finish within {options.MaxPolls} polls.");
    }
}
