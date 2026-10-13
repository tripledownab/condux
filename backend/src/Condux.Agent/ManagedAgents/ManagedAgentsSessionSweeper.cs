namespace Condux.Agent.ManagedAgents;

/// <summary>
/// Removes the vendor sessions a fix run left behind. Deleting a session removes its record, its events and
/// its sandbox, which holds the cloned repository, so a session left in place keeps the customer's source at
/// the vendor. The gateway deletes a session when the run ends, but the vendor refuses to delete a session
/// that is still running, a stopped session is still winding down when the stop asks, and a conductor that
/// dies mid-run asks nothing at all. This pass is what makes the removal happen anyway.
///
/// It only touches sessions of the agents the gateway provisions, asked of the vendor per agent so that
/// other sessions under the same key cannot fill the pages it reads, and it checks each session's title as
/// well. It takes only those created before a cutoff the caller chooses, which must be past the point
/// where any run still polls them. Settled sessions are deleted. Working ones are interrupted, which is the
/// vendor's only way to stop one, and deleted on a later pass once idle. So is a session in a status this
/// version does not know, which may still be working: a listing filtered to the known statuses would never
/// return it, so it has a pass of its own over every status.
/// </summary>
public sealed class ManagedAgentsSessionSweeper(ManagedAgentsClient client, ManagedAgentsOptions options)
{
    // A runaway bound per kind per pass; the next pass takes the rest. Each kind has its own, so a backlog
    // of one cannot crowd out another.
    private const int MaxPages = 10;

    /// <summary>One pass over sessions created before <paramref name="cutoff"/>: deletes the settled ones
    /// and stops the rest. A failure ends only the pass it happened in, so one agent's failure cannot keep
    /// another's sessions at the vendor, and the first one comes back with the counts rather than being
    /// thrown, so the work that did succeed is still reported. A failure to list the agents ends the sweep,
    /// since there is nothing to pass over, and comes back the same way.</summary>
    public async Task<SessionSweepResult> SweepAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        var (deleted, stopped) = (0, 0);
        Exception? failure = null;
        async Task<int> PassAsync(Func<Task<int>> pass)
        {
            try
            {
                return await pass();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failure ??= ex;
                return 0;
            }
        }

        IReadOnlyList<string> agentIds;
        try
        {
            agentIds = await ListOwnAgentsAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return new SessionSweepResult(0, 0, ex);
        }
        foreach (var agentId in agentIds)
        {
            deleted += await PassAsync(() => ForEachOwnAsync(
                agentId, WireSessionStatus.Settled, _ => true, cutoff, client.DeleteSessionAsync, ct));
            stopped += await PassAsync(() => ForEachOwnAsync(
                agentId, WireSessionStatus.Working, _ => true, cutoff, client.InterruptSessionAsync, ct));
            // Every status, keeping only the unknown ones, which are interrupted and then deleted: one may be
            // working, which the delete is refused for until a later pass, or finished, which the interrupt
            // is refused for. A backlog of known sessions can crowd this pass but not the two above.
            stopped += await PassAsync(() => ForEachOwnAsync(
                agentId, [], s => !WireSessionStatus.IsKnown(s.Status), cutoff, StopAsync, ct));
        }
        return new SessionSweepResult(deleted, stopped, failure);
    }

    // The delete is tried whatever the interrupt met, a refusal or a failed connection alike.
    private async Task StopAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            await client.InterruptSessionAsync(sessionId, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
        }
        await client.DeleteSessionAsync(sessionId, ct);
    }

    // The pinned agent, or every agent the provisioner names, archived ones included, since archiving an
    // agent does not remove its sessions.
    private async Task<IReadOnlyList<string>> ListOwnAgentsAsync(CancellationToken ct) =>
        options.AgentId is { } pinned
            ? [pinned]
            : [.. (await client.ListAgentsAsync(ct))
                .Where(a => a.Name.StartsWith(ManagedAgentsProvisioner.AgentNamePrefix, StringComparison.Ordinal))
                .Select(a => a.Id)];

    private async Task<int> ForEachOwnAsync(
        string agentId, IReadOnlyList<string> statuses, Func<WireSessionSummary, bool> take, DateTimeOffset cutoff,
        Func<string, CancellationToken, Task> act, CancellationToken ct)
    {
        var done = 0;
        string? page = null;
        for (var i = 0; i < MaxPages; i++)
        {
            var list = await client.ListSessionsAsync(agentId, statuses, cutoff, page, ct);
            foreach (var session in list.Data.Where(s => IsOwn(s) && take(s)))
            {
                try
                {
                    await act(session.Id, ct);
                    done++;
                }
                // One session the vendor refuses, say one that started running again, must not stop the
                // pass; it is met again on the next one. Any other failure ends the pass and is reported.
                catch (ManagedAgentsApiException)
                {
                }
            }
            if (list.NextPage is null or "")
            {
                break;
            }
            page = list.NextPage;
        }
        return done;
    }

    private static bool IsOwn(WireSessionSummary session) =>
        session.Title?.StartsWith(ManagedAgentsGateway.SessionTitlePrefix, StringComparison.Ordinal) == true;
}

/// <summary>What one session sweep did: sessions deleted, sessions stopped (interrupted, and for a status
/// the sweep does not know also deleted), and the first failure that ended a pass early, or null.</summary>
public sealed record SessionSweepResult(int Deleted, int Stopped, Exception? Failure);
