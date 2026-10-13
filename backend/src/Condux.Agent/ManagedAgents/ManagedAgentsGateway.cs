using System.Collections.Concurrent;
using Condux.Core.FixEngine;
using Condux.Core.SourceControl;

namespace Condux.Agent.ManagedAgents;

/// <summary>
/// The vendor-hosted fix backend (ADR-0038): Anthropic Managed Agents runs the loop and the sandbox with
/// the whole repository mounted, and this gateway starts a session, polls it, turns its final answer into a
/// draft PR, and interrupts it when the run is stopped. Safety is by construction: the session's repo mount
/// uses a READ-ONLY single-repo token (the source host refuses a push from the sandbox), and all git runs
/// host-side via the shared <see cref="DraftPrPublisher"/>, so the write-capable token never leaves this
/// process. The vendor session id IS the run id, so this holds only the runs it started and a memo of results.
/// </summary>
public sealed partial class ManagedAgentsGateway(
    ManagedAgentsClient client, ISourceHostTokens tokens, ISourceHostClient repo,
    ManagedAgentsOptions options) : IAgentGateway
{
    private const string MountPath = "/workspace/repo";
    // Every session's title starts with this, which is how the session sweep tells ours from others.
    internal const string SessionTitlePrefix = "condux fix ";

    private readonly ConcurrentDictionary<string, StartedRun> runs = new();
    private readonly ConcurrentDictionary<string, AgentRunProgress> finalized = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> finalizing = new();
    private readonly ManagedAgentsProvisioner provisioner = new(client, options);

    public string BackendName => "anthropic-managed-agents";

    public async Task<AgentRun> StartAsync(AgentRunSpec spec, CancellationToken ct = default)
    {
        SourceHostGuards.RequireInstallation(tokens, spec);

        var agent = await provisioner.GetAgentAsync(spec.Model, ct);
        var envId = await provisioner.GetEnvironmentIdAsync(ct);
        // Read-only by construction: the sandbox can clone this one repo and nothing else — a push is
        // refused by the source host itself, which is what makes draft-PR-only structural here.
        var readOnlyToken = await tokens.GetReadOnlyAsync(spec.InstallationId, spec.RepoFullName, ct);
        // The session clones this exact commit rather than the branch, and the run keeps it: the session can
        // take most of an hour, and a file changed on the branch meanwhile would otherwise be published as
        // the session's version, quietly undoing the change. The read-only token is enough to read it.
        var baseCommit = await repo.GetBranchHeadShaAsync(readOnlyToken, spec.RepoFullName, spec.BaseBranch, ct);

        // The repo name and the branch go into a URL the vendor clones, and neither is validated where it
        // is stored, so both go through the same rule the source-host client applies rather than being
        // interpolated. Without it a name carrying a `..` segment would resolve to a different repository.
        var cloneUrl = $"https://github.com/{RepoPaths.EscapeSegments(spec.RepoFullName)}";
        var session = await client.CreateSessionAsync(new WireSessionCreate(
            agent, envId, $"{SessionTitlePrefix}{spec.IssueId}",
            [new WireRepositoryResource(
                cloneUrl, readOnlyToken,
                new WireCheckout("commit", Sha: baseCommit), MountPath)],
            [new WireUserMessage([new WireTextContent(BuildUserMessage(spec, baseCommit))])],
            new WireBudget(new WireMoney(
                ManagedAgentsClient.MinorUnits(options.MaxSessionUsd), "USD"))), ct);

        runs[session.Id] = new StartedRun(spec, baseCommit);
        return new AgentRun(session.Id);
    }

    public async Task<AgentRunProgress> PollAsync(string runId, CancellationToken ct = default)
    {
        if (finalized.TryGetValue(runId, out var done))
        {
            return done;
        }
        if (!runs.TryGetValue(runId, out var started))
        {
            throw new KeyNotFoundException($"Unknown agent run '{runId}'.");
        }

        WireSession session;
        try
        {
            session = await client.GetSessionAsync(runId, ct);
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            return AgentRunProgress.StillRunning;
        }

        if (WireSessionStatus.Working.Contains(session.Status))
        {
            return AgentRunProgress.StillRunning;
        }
        // "idle" is decided by FinalizeAsync from why the session stopped. Anything else ends the run with
        // the vendor's word for it, and a status this version does not know may mean a session still
        // working and spending, so that one is stopped too; failing the run does not stop it, and the
        // session sweep would reach it only past its cutoff.
        if (session.Status != WireSessionStatus.Idle)
        {
            if (session.Status != WireSessionStatus.Terminated)
            {
                await StopSessionAsync(runId, ct);
            }
            return Remember(runId, AgentRunProgress.Failed($"Managed Agents session ended: {session.Status}."));
        }

        var gate = finalizing.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (finalized.TryGetValue(runId, out done))
            {
                return done;
            }
            var progress = await FinalizeAsync(runId, started, session, ct);
            return progress.Status == AgentRunStatus.Running ? progress : Remember(runId, progress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Remember(runId, AgentRunProgress.Failed(ex));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AgentRunProgress> CancelAsync(
        string runId, CancellationToken ct = default, bool publishFinished = false)
    {
        // Only a session this gateway started. Any other id would be an interrupt sent to someone
        // else's session, and a memo kept for it for ever.
        if (!runs.ContainsKey(runId) && !finalized.ContainsKey(runId))
        {
            return AgentRunProgress.Failed(new KeyNotFoundException($"Unknown agent run '{runId}'."));
        }

        // When the caller allows it, a session that already finished its turn is finalized here as a poll
        // would finalize it, since its answer is paid for and a success wins on every path. Otherwise the
        // session is stopped: it does not stop by itself, and runs and spends up to its budget.
        var gate = finalizing.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (finalized.TryGetValue(runId, out var done))
            {
                return done;
            }
            if (publishFinished && runs.TryGetValue(runId, out var started)
                && await TryFinalizeIdleAsync(runId, started, ct) is { } ended)
            {
                return ended;
            }
            await StopSessionAsync(runId, ct);
            return Remember(runId, AgentRunProgress.Failed(
                new TimeoutException("The run was stopped before it finished.")));
        }
        finally
        {
            gate.Release();
        }
    }

    // Interrupt, then delete, each best effort: the run is over for Condux whatever the vendor answers. The
    // delete is usually refused while an interrupted session winds down, and a stop must not wait, since it
    // can run on shutdown. ManagedAgentsSessionSweeper deletes what is left once nothing polls it.
    private async Task StopSessionAsync(string runId, CancellationToken ct)
    {
        try
        {
            await client.InterruptSessionAsync(runId, ct);
        }
        catch (Exception)
        {
        }
        await TryDeleteSessionAsync(runId, ct);
    }

    // The provider's poll interval is the backoff and its poll ceiling the limit, so a throttled or
    // timed-out read is not a failed run. Failing it would also abandon a session still spending, or
    // throw away one that has finished. A server error is not retried: it fails the run at once with the
    // vendor's answer, where retrying it would report an outage as a timeout at the poll budget.
    private static bool IsTransient(Exception ex, CancellationToken ct) =>
        ex is ManagedAgentsApiException { IsRateLimit: true }
            || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private AgentRunProgress Remember(string runId, AgentRunProgress progress)
    {
        finalized[runId] = progress;
        runs.TryRemove(runId, out _);
        return progress;
    }

    /// <summary>A run this gateway started, and the commit its session cloned.</summary>
    private sealed record StartedRun(AgentRunSpec Spec, string BaseCommit);

    private static string BuildUserMessage(AgentRunSpec spec, string baseCommit)
    {
        // spec.Prompt arrives already fenced from the assembler. What is added here is not our text
        // either: a scoped path only ever passed a CONTAINMENT check, and RepoEndpoints stores a
        // repository name and a default branch with no shape check at all. Each is interpolated rather
        // than fenced because it has to read as the task, so neutralizing is the whole defence.
        var scoped = spec.ScopedPaths.Select(UntrustedText.Neutralize);
        var paths = spec.ScopedPaths.Count > 0
            ? $"\n\nStart with these files (from the error's stack trace): {string.Join(", ", scoped)}"
            : "";
        return $"{spec.Prompt}\n\nRepository: {UntrustedText.Neutralize(spec.RepoFullName)} "
            + $"(base branch: {UntrustedText.Neutralize(spec.BaseBranch)}, checked out at commit {baseCommit}), "
            + $"mounted read-only at {MountPath}.{paths}";
    }
}
