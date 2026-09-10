using System.Collections.Concurrent;
using Condux.Core.FixEngine;
using Condux.Core.SourceControl;

namespace Condux.Agent.ManagedAgents;

/// <summary>
/// The vendor-hosted fix backend (ADR-0038): Anthropic Managed Agents runs the loop and the sandbox with
/// the whole repository mounted, and this gateway only starts a session, polls it, and turns its
/// structured final answer into a draft PR. Safety is by construction: the session's repo mount uses a
/// READ-ONLY single-repo token (a push from the sandbox is refused by the source host), and all git
/// runs host-side via the shared <see cref="DraftPrPublisher"/> — the write-capable token never leaves
/// this process. Poll-driven and nearly stateless: the vendor session id IS the run id, so this process
/// holds only the per-run spec and a memo of finalized results.
/// </summary>
public sealed class ManagedAgentsGateway(
    ManagedAgentsClient client, ISourceHostTokens tokens, ISourceHostClient repo,
    ManagedAgentsOptions options) : IAgentGateway
{
    // The persisted agent config's system text. The final-answer contract is the same JSON the one-shot
    // path uses, so FixPlanParser is reused verbatim on the way out.
    private const string SystemPrompt =
        "You are the Condux Conductor, a senior engineer fixing a production error. The repository is "
        + "mounted READ-ONLY in your workspace (the user message names the path) — explore it freely, "
        + "run the tests if the environment allows, and edit your working copy to develop the fix, but "
        + "never attempt git push or any other remote write; the checkout cannot accept one. Address the "
        + "root cause, change only what the fix requires, and keep the existing code style. Add or update "
        + "a test that fails without your change and passes with it. If you conclude the code is already "
        + "correct and no such test can be written, say so in the summary and change nothing. Change at "
        + "most 10 files; if a correct fix needs more, stop and explain in the summary instead. End your "
        + "final message with ONLY a JSON object, no code fences and no prose after it: {\"summary\": "
        + "\"what the fix does and why\", \"files\": [{\"path\": \"repo/relative/path\", \"contents\": "
        + "\"the COMPLETE new file contents\"}]}. Every entry in files must contain the full file, not a diff.";

    private const string MountPath = "/workspace/repo";

    private readonly ConcurrentDictionary<string, AgentRunSpec> specs = new();
    private readonly ConcurrentDictionary<string, AgentRunProgress> finalized = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> finalizing = new();
    private readonly ConcurrentDictionary<string, string> agentIds = new();
    // One at a time through vendor-object provisioning: the fix and CVE workers share this singleton,
    // and a concurrent first run must not create the same agent or environment twice.
    private readonly SemaphoreSlim provisioning = new(1, 1);
    private string? environmentId;

    public string BackendName => "anthropic-managed-agents";

    public async Task<AgentRun> StartAsync(AgentRunSpec spec, CancellationToken ct = default)
    {
        SourceHostGuards.RequireInstallation(tokens, spec);

        var agentId = options.AgentId ?? await GetOrCreateAgentAsync(spec.Model, ct);
        var envId = options.EnvironmentId ?? await GetOrCreateEnvironmentAsync(ct);
        // Read-only by construction: the sandbox can clone this one repo and nothing else — a push is
        // refused by the source host itself, which is what makes draft-PR-only structural here.
        var readOnlyToken = await tokens.GetReadOnlyAsync(spec.InstallationId, spec.RepoFullName, ct);

        // The repo name and the branch go into a URL the vendor clones, and neither is validated where it
        // is stored, so both go through the same rule the source-host client applies rather than being
        // interpolated. Without it a name carrying a `..` segment would resolve to a different repository.
        var cloneUrl = $"https://github.com/{RepoPaths.EscapeSegments(spec.RepoFullName)}";
        var session = await client.CreateSessionAsync(new WireSessionCreate(
            agentId, envId, $"condux fix {spec.IssueId}",
            [new WireRepositoryResource(
                cloneUrl, readOnlyToken,
                new WireCheckout("branch", spec.BaseBranch), MountPath)],
            [new WireUserMessage([new WireTextContent(BuildUserMessage(spec))])],
            new WireBudget(new WireMoney(
                ManagedAgentsClient.MinorUnits(options.MaxSessionUsd), "USD"))), ct);

        specs[session.Id] = spec;
        return new AgentRun(session.Id);
    }

    public async Task<AgentRunProgress> PollAsync(string runId, CancellationToken ct = default)
    {
        if (finalized.TryGetValue(runId, out var done))
        {
            return done;
        }
        if (!specs.TryGetValue(runId, out var spec))
        {
            throw new KeyNotFoundException($"Unknown agent run '{runId}'.");
        }

        WireSession session;
        try
        {
            session = await client.GetSessionAsync(runId, ct);
        }
        catch (ManagedAgentsApiException ex) when (ex.IsRateLimit)
        {
            // The provider's poll interval is the backoff; a throttled poll is not a failed run.
            return AgentRunProgress.StillRunning;
        }

        if (session.Status == "running")
        {
            return AgentRunProgress.StillRunning;
        }
        // "idle" means the agent finished its turn (sessions are conversational); anything else —
        // failed, expired, a budget stop — ends the run with the vendor's word for why.
        if (session.Status != "idle")
        {
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
            var progress = await FinalizeAsync(runId, spec, session, ct);
            return Remember(runId, progress);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Remember(runId, AgentRunProgress.Failed(ex.Message));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AgentRunProgress> FinalizeAsync(
        string runId, AgentRunSpec spec, WireSession session, CancellationToken ct)
    {
        var events = await client.GetSessionEventsAsync(runId, ct);
        var finalText = events
            .Where(e => e.Type == "agent.message")
            .SelectMany(e => e.Content ?? [])
            .Select(c => c.Text)
            .LastOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?? throw new InvalidOperationException("The agent session produced no final message.");

        // The plan comes from an agent that browsed a whole repository, so its paths are untrusted input
        // to the git tail. The check that keeps them inside the repo is DraftPrPublisher's, below, which
        // the single-shot gateway shares: this gateway used to hold its own copy and was therefore the
        // only one of the two that had it.
        var plan = FixPlanParser.Parse(finalText);
        var token = await tokens.GetAsync(spec.InstallationId, ct);
        var (branch, prUrl, summary) = await DraftPrPublisher.PublishAsync(
            repo, token, spec, runId,
            plan.Files.ToDictionary(change => change.Path, change => change.Contents, StringComparer.Ordinal),
            plan.Summary, prefetched: null, ct: ct);

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

        try
        {
            await client.DeleteSessionAsync(runId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cleanup only — the run already concluded; an undeleted session is vendor-side clutter.
        }
        return progress;
    }

    private AgentRunProgress Remember(string runId, AgentRunProgress progress)
    {
        finalized[runId] = progress;
        specs.TryRemove(runId, out _);
        return progress;
    }

    private async Task<string> GetOrCreateAgentAsync(string model, CancellationToken ct)
    {
        if (agentIds.TryGetValue(model, out var cached))
        {
            return cached;
        }
        await provisioning.WaitAsync(ct);
        try
        {
            if (agentIds.TryGetValue(model, out cached))
            {
                return cached;
            }
            var name = $"condux-conductor-{model}";
            var existing = (await client.ListAgentsAsync(ct))
                .FirstOrDefault(a => a.Name == name && a.ArchivedAt is null);
            var id = existing?.Id ?? (await client.CreateAgentAsync(name, model, SystemPrompt, ct)).Id;
            agentIds[model] = id;
            return id;
        }
        finally
        {
            provisioning.Release();
        }
    }

    private async Task<string> GetOrCreateEnvironmentAsync(CancellationToken ct)
    {
        if (environmentId is not null)
        {
            return environmentId;
        }
        await provisioning.WaitAsync(ct);
        try
        {
            environmentId ??= (await client.ListEnvironmentsAsync(ct))
                    .FirstOrDefault(e => e.Name == "condux-conductor" && e.ArchivedAt is null)?.Id
                ?? (await client.CreateEnvironmentAsync("condux-conductor", ct)).Id;
            return environmentId;
        }
        finally
        {
            provisioning.Release();
        }
    }

    private static string BuildUserMessage(AgentRunSpec spec)
    {
        var paths = spec.ScopedPaths.Count > 0
            ? $"\n\nStart with these files (from the error's stack trace): {string.Join(", ", spec.ScopedPaths)}"
            : "";
        return $"{spec.Prompt}\n\nRepository: {spec.RepoFullName} (base branch: {spec.BaseBranch}), "
            + $"mounted read-only at {MountPath}.{paths}";
    }
}
