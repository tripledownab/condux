using Condux.Core.FixEngine;
using Condux.Core.SourceControl;

namespace Condux.Agent.ManagedAgents;

// What the gateway does once a session has gone idle: decide from the session's stop reason whether the
// agent finished, and if it did, turn its final answer into a draft PR.
public sealed partial class ManagedAgentsGateway
{
    private const string IdleEvent = "session.status_idle";
    private const string AgentMessageEvent = "agent.message";
    private const string InterruptEvent = "user.interrupt";

    // Ours, not the vendor's: the reason given to an end_turn that an interrupt caused.
    private const string Interrupted = "interrupted";
    private const string EndTurn = "end_turn";

    // Idle is not the same as finished. The session.status_idle event carries why the session stopped, and
    // a draft PR is opened only on end_turn, the reason a turn that completes reports. A session that
    // reaches its budget goes idle with budget_reached, and one waiting on a tool confirmation with
    // requires_action. Its last message may be complete even then, but nothing says so, and a PR opened
    // from unfinished work is published, so those runs fail. end_turn is not proof either: the vendor
    // reports it for a turn ended by an interrupt too, so an interrupt recorded during the turn fails it.
    private async Task<AgentRunProgress> FinalizeAsync(
        string runId, StartedRun started, WireSession session, CancellationToken ct)
    {
        var spec = started.Spec;
        IReadOnlyList<WireEvent> events;
        try
        {
            events = await client.GetSessionEventsAsync(runId, ct);
        }
        catch (Exception ex) when (IsTransient(ex, ct))
        {
            return AgentRunProgress.StillRunning;
        }
        // Listed newest first, so the first idle event is why the session is idle now. With none listed,
        // nothing says the session finished, so it is read again on the next poll, which the poll budget
        // bounds.
        var idle = events.FirstOrDefault(e => e.Type == IdleEvent);
        if (idle is null)
        {
            return AgentRunProgress.StillRunning;
        }
        // An idle event with no reason we can read is not a finished turn, and polling again would read the
        // same event, so it fails now rather than at the poll budget. And an interrupt ends a turn with
        // end_turn too, so a turn with a user.interrupt recorded during it is not a finished one either.
        var stopReason = idle.StopReason?.Type ?? "unreadable";
        if (stopReason == EndTurn && WasInterrupted(events))
        {
            stopReason = Interrupted;
        }
        if (stopReason != EndTurn)
        {
            // The run ends here, so the session goes too: its sandbox holds the cloned repository.
            await TryDeleteSessionAsync(runId, ct);
            throw new FixFailedException(
                ToFailureReason(stopReason), $"Managed Agents session stopped before finishing: {stopReason}.");
        }

        // Newest message first, and within one message its last block with text, as before.
        var finalText = events
            .Where(e => e.Type == AgentMessageEvent)
            .Select(e => (e.Content ?? []).Select(c => c.Text).LastOrDefault(t => !string.IsNullOrWhiteSpace(t)))
            .FirstOrDefault(t => t is not null)
            ?? throw new FixFailedException(FixFailureReason.NoUsableFix, "The agent session produced no final message.");

        // The plan comes from an agent that browsed a whole repository, so its paths are untrusted input
        // to the git tail. The check that keeps them inside the repo is DraftPrPublisher's, below, which
        // the single-shot gateway shares: this gateway used to hold its own copy and was therefore the
        // only one of the two that had it.
        var plan = FixPlanParser.Parse(finalText);
        var token = await tokens.GetAsync(spec.InstallationId, ct);
        var (branch, prUrl, summary) = await DraftPrPublisher.PublishAsync(
            repo, token, spec,
            plan.Files.ToDictionary(change => change.Path, change => change.Contents, StringComparer.Ordinal),
            plan.Summary, await ToSeenAsync(token, started, plan.Files.Select(change => change.Path), ct),
            // The session could read any file in its checkout whole, so nothing was left unread for want of it.
            leftUnread: [], prefetched: null, ct: ct);

        // Cache tokens dominate a CMA session (probed live), so raw input alone would misrepresent the
        // work; fold them into the input count until actual-cost reporting lands (ADR-0038 slice 6).
        var usage = session.Usage;
        var progress = AgentRunProgress.Done(branch, prUrl, summary) with
        {
            InputTokens = usage is null
                ? 0
                : usage.InputTokens + usage.CacheReadInputTokens
                    + (usage.CacheCreation?.Ephemeral5mInputTokens ?? 0)
                    + (usage.CacheCreation?.Ephemeral1hInputTokens ?? 0),
            OutputTokens = usage?.OutputTokens ?? 0,
        };

        // The pull request exists, so nothing here may fail the run, not a timeout and not a shutdown either.
        await TryDeleteSessionAsync(runId, ct);
        return progress;
    }

    // Newest first: the events after the latest idle event, up to the idle event before it, are that
    // turn's. An interrupt from an earlier turn says nothing about this one.
    private static bool WasInterrupted(IReadOnlyList<WireEvent> events) => events
        .SkipWhile(e => e.Type != IdleEvent)
        .Skip(1)
        .TakeWhile(e => e.Type != IdleEvent)
        .Any(e => e.Type == InterruptEvent);

    // At a stop that ran out of time: the run's answer if its session had already gone idle, or null when it
    // had not, its status could not be read, its events read was throttled or timed out, or no idle event
    // is listed yet, which leaves the caller to stop the session. Any other failure while finalizing, an
    // events read the vendor refuses or an idle event whose reason cannot be read among them, fails the run
    // as a poll would. Not asked on a shutdown: these are source-host writes, and one cut off part way leaves
    // a branch or a PR that no run records.
    private async Task<AgentRunProgress?> TryFinalizeIdleAsync(string runId, StartedRun started, CancellationToken ct)
    {
        WireSession session;
        try
        {
            session = await client.GetSessionAsync(runId, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        if (session.Status != WireSessionStatus.Idle)
        {
            return null;
        }
        try
        {
            var progress = await FinalizeAsync(runId, started, session, ct);
            return progress.Status == AgentRunStatus.Running ? null : Remember(runId, progress);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Polling has ended, so this failure is the run's end. The session is idle, so a delete is
            // accepted and removes the sandbox with the cloned repository.
            await TryDeleteSessionAsync(runId, ct);
            return Remember(runId, AgentRunProgress.Failed(ex));
        }
    }

    // A budget stop leaves no change that could be applied within the run's limits. A reason this version
    // does not know is reported as exactly that.
    private static FixFailureReason ToFailureReason(string stopReason) => stopReason switch
    {
        "budget_reached" => FixFailureReason.NoUsableFix,
        "refusal" => FixFailureReason.ModelRefused,
        "requires_action" => FixFailureReason.AgentBlocked,
        Interrupted => FixFailureReason.StoppedExternally,
        "retries_exhausted" => FixFailureReason.ModelUnavailable,
        _ => FixFailureReason.Unknown,
    };

    // Best effort. A session left behind is removed by ManagedAgentsSessionSweeper.
    private async Task TryDeleteSessionAsync(string runId, CancellationToken ct)
    {
        try
        {
            await client.DeleteSessionAsync(runId, ct);
        }
        catch (Exception)
        {
        }
    }

    // The session had the whole repository mounted at the commit it cloned, so each file the plan changes
    // was seen as its blob at that commit. One that is absent there, or is not a file, was not seen, and
    // the publisher refuses to replace it if it exists now.
    private async Task<FilesSeen> ToSeenAsync(
        string token, StartedRun started, IEnumerable<string> paths, CancellationToken ct)
    {
        var seen = new List<RepoFile>();
        foreach (var path in paths)
        {
            try
            {
                if (await repo.GetFileAsync(token, started.Spec.RepoFullName, path, started.BaseCommit, ct) is { } file)
                {
                    seen.Add(file);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
        return FilesSeen.Only(seen);
    }
}
