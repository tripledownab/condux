namespace Condux.Core.FixEngine;

/// <summary>Timing for the poll loop: how often to poll a run and how many times before giving up.</summary>
public sealed record ManagedAgentOptions(TimeSpan PollInterval, int MaxPolls)
{
    // 5s between polls, up to 120 polls (~10 minutes) before a run is declared timed out. One place to
    // change the run budget; the Conductor can override from config.
    public static ManagedAgentOptions Default { get; } = new(TimeSpan.FromSeconds(5), 120);

    /// <summary>How long the provider polls a run, by the clock, before stopping it. Polling also stops
    /// after <see cref="MaxPolls"/> polls, whichever comes first. Starting the run, the poll and the wait in
    /// progress when the budget runs out, and the stop all come on top.</summary>
    public TimeSpan PollBudget => PollInterval * MaxPolls;
}

/// <summary>
/// The provider-agnostic orchestration for one fix run: start an agentic run on an <see cref="IAgentGateway"/>,
/// poll it to completion, and map the outcome to a <see cref="FixResult"/> (the draft PR). A run that fails,
/// times out, or reports success without a PR URL throws, so the <see cref="FixOrchestrator"/> records it as
/// FAILED and audits it. The poll delay and the clock are injected so tests drive the loop without ever
/// sleeping.
/// Whenever it stops polling without a final answer, it stops the run too, since a backend working
/// between polls would otherwise carry on and could open a draft PR that no run records.
/// </summary>
public sealed class ManagedAgentFixProvider : IFixProvider
{
    private readonly IAgentGateway gateway;
    private readonly ManagedAgentOptions options;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private readonly TimeProvider clock;

    public ManagedAgentFixProvider(
        IAgentGateway gateway,
        ManagedAgentOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? clock = null)
    {
        this.gateway = gateway;
        this.options = options ?? ManagedAgentOptions.Default;
        this.delay = delay ?? Task.Delay;
        this.clock = clock ?? TimeProvider.System;
    }

    public string Name => gateway.BackendName;

    public async Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
    {
        var spec = new AgentRunSpec(
            request.IssueId, request.RepoFullName, request.BaseBranch, request.Prompt, request.Model)
        {
            RunId = request.RunId,
            ScopedPaths = request.ScopedPaths,
            InstallationId = request.InstallationId,
            OrgId = request.OrgId,
        };
        var run = await gateway.StartAsync(spec, cancellationToken);

        // A poll that times out reads as still running, so counting polls alone would let a slow backend
        // run many times longer than the budget, past the point where the run is treated as abandoned.
        // A zero budget, which only a test configures, polls by count alone.
        var deadline = clock.GetUtcNow() + options.PollBudget;
        var settled = false;
        try
        {
            for (var attempt = 0; attempt < options.MaxPolls && !IsPastDeadline(deadline); attempt++)
            {
                var progress = await gateway.PollAsync(run.RunId, cancellationToken);
                if (progress.Status != AgentRunStatus.Running)
                {
                    settled = true;
                    return ToResult(progress);
                }
                await delay(options.PollInterval, cancellationToken);
            }

            // Out of polls or out of time. A run that finished before the stop landed keeps its result, or its draft PR
            // would exist with no run recording it. One that ended for a reason of its own as the stop landed keeps
            // that reason, which says more than a timeout; a reason of Unknown is reported as the timeout, whose
            // notice says to check the repository. The loop ends on the clock without reading the token, so a
            // shutdown can arrive here too, and then nothing is published: it would start source-host writes the
            // process may not live to finish. The token is read once, here, and not again during the stop.
            settled = true;
            var stopped = await StopAsync(run.RunId, publishFinished: !cancellationToken.IsCancellationRequested);
            return stopped.Status == AgentRunStatus.Succeeded
                || stopped.Reason is not (FixFailureReason.Unknown or FixFailureReason.TimedOut)
                ? ToResult(stopped)
                : throw new TimeoutException($"Agent run did not finish within {options.PollBudget}.");
        }
        catch (Exception) when (!settled)
        {
            // Polling ended by an exception, a shutdown or a lost lease among them. The run is stopped,
            // and one that had already opened its draft PR returns that result: the PR exists, and
            // rethrowing would leave it recorded nowhere, for a retry to open a second one.
            var stopped = await StopAsync(run.RunId);
            if (stopped.Status == AgentRunStatus.Succeeded && !string.IsNullOrEmpty(stopped.PrUrl))
            {
                return ToResult(stopped);
            }
            throw;
        }
    }

    private bool IsPastDeadline(DateTimeOffset deadline) =>
        options.PollBudget > TimeSpan.Zero && clock.GetUtcNow() >= deadline;

    private static FixResult ToResult(AgentRunProgress progress) => progress.Status switch
    {
        AgentRunStatus.Succeeded when string.IsNullOrEmpty(progress.PrUrl) =>
            throw new FixFailedException(FixFailureReason.NoUsableFix, "Agent run reported success without a draft PR URL."),
        AgentRunStatus.Succeeded => new FixResult(progress.Branch, progress.PrUrl, progress.Summary)
        {
            InputTokens = progress.InputTokens,
            OutputTokens = progress.OutputTokens,
        },
        _ => throw new FixFailedException(progress.Reason, $"Agent run failed: {progress.Error}"),
    };

    // Stops a run polling has left. Never with the caller's token, which may be the one that just fired.
    // A stop that fails is reported as an unfinished run, so it can neither replace the exception that
    // ended polling nor turn a timeout into some other failure.
    private async Task<AgentRunProgress> StopAsync(string runId, bool publishFinished = false)
    {
        try
        {
            return await gateway.CancelAsync(runId, CancellationToken.None, publishFinished);
        }
        catch (Exception ex)
        {
            // By message only: classifying it would give a failed stop a reason of its own, which the
            // ceiling would then report in place of the timeout.
            return AgentRunProgress.Failed(ex.Message);
        }
    }
}
