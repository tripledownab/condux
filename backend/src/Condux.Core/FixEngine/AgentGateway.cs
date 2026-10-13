namespace Condux.Core.FixEngine;

/// <summary>Where an agentic fix run is in its lifecycle. Deliberately small: a run is either still going,
/// finished with a draft PR, or failed.</summary>
public enum AgentRunStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
}

/// <summary>The scoped, scrubbed inputs for one agentic fix run, handed to a gateway to start it. The
/// scrubbing and file scoping happen upstream (RequestFix, #63); here it is already safe to send.</summary>
public sealed record AgentRunSpec(string IssueId, string RepoFullName, string BaseBranch, string Prompt, string Model)
{
    /// <summary>The fix run's own id, which names its branch (<see cref="FixBranches"/>). Not the gateway's
    /// run id, which the run row does not keep.</summary>
    public required Guid RunId { get; init; }

    /// <summary>The repo-relative files the fix should focus on (never the whole repo).</summary>
    public IReadOnlyList<string> ScopedPaths { get; init; } = [];

    /// <summary>The org's GitHub App installation for minting a repo-scoped token (0 = none).</summary>
    public long InstallationId { get; init; }

    /// <summary>The org the run belongs to, so the gateway can resolve its BYO-key config (#65). 0 uses
    /// the platform default.</summary>
    public long OrgId { get; init; }
}

/// <summary>A started run, identified by an opaque id the gateway uses to report progress.</summary>
public sealed record AgentRun(string RunId);

/// <summary>A poll of a run: its status plus, on success, the branch and draft PR it opened (or the error
/// on failure). Use the factory members so a caller never builds an inconsistent progress value.</summary>
public sealed record AgentRunProgress(AgentRunStatus Status, string Branch, string PrUrl, string Summary, string? Error)
{
    /// <summary>Model tokens the run consumed — the raw material for AI-fix cost metering (#100).
    /// Zero for backends that call no model (fake/simulated).</summary>
    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public static AgentRunProgress StillRunning { get; } = new(AgentRunStatus.Running, "", "", "", null);

    public static AgentRunProgress Done(string branch, string prUrl, string summary) =>
        new(AgentRunStatus.Succeeded, branch, prUrl, summary, null);

    /// <summary>Why a failed run failed, carried across the start and poll seam, where the exception that
    /// knew it is long gone.</summary>
    public FixFailureReason Reason { get; init; }

    public static AgentRunProgress Failed(string error) =>
        new(AgentRunStatus.Failed, "", "", "", error);

    public static AgentRunProgress Failed(Exception exception) =>
        Failed(exception.Message) with { Reason = FixFailures.Classify(exception) };
}

/// <summary>
/// The agentic backend behind a fix run: start a run, poll it to completion, and stop it if polling ends
/// first. This is the seam that isolates the provider-agnostic orchestration
/// (<see cref="ManagedAgentFixProvider"/>) from vendor specifics. Anthropic Managed Agents (default) and
/// the bring-your-own-model backends are each a gateway implementation; the safety guarantees (sandboxed
/// edits, credential isolation, draft-PR-only) live inside each implementation, not the orchestration.
/// </summary>
public interface IAgentGateway
{
    /// <summary>Identifies the backend, surfaced as the fix suggestion's provider name.</summary>
    string BackendName { get; }

    /// <summary>Begin an agentic fix run for the spec; returns the handle to poll.</summary>
    Task<AgentRun> StartAsync(AgentRunSpec spec, CancellationToken cancellationToken = default);

    /// <summary>Report the current state of a run.</summary>
    Task<AgentRunProgress> PollAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop a run nobody will poll again, and report the state it stopped in. A backend that does its
    /// work between polls would otherwise carry on and could open a draft PR no run records. A run that
    /// finished before the stop took effect reports that result, so the caller can record a PR it did
    /// open. Each backend bounds how long it waits for the run to stop, a stop that publishes only by the
    /// timeouts of the calls it makes. One window stays open: a stop that lands while the source host is
    /// creating the PR ends the run before it reads the answer.
    /// </summary>
    /// <param name="publishFinished">Whether a run whose work is done but whose draft PR is not yet opened
    /// may open it now, rather than be discarded. Only when polling ran out of time and no shutdown had begun
    /// by then. A shutdown that begins later, while the stop reads the session or writes to the source host,
    /// does not stop it, and can cut the writes off, leaving a branch or a PR that no run records.</param>
    Task<AgentRunProgress> CancelAsync(
        string runId, CancellationToken cancellationToken = default, bool publishFinished = false);
}
