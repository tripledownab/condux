using System.Text.Json.Serialization;

namespace Condux.Core.FixEngine;

/// <summary>Lifecycle of a Conductor fix run. Values match <c>condux.fix.v1</c> <c>FixStatus</c>.</summary>
public enum FixStatus
{
    Unspecified = 0,
    Pending = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Cancelled = 5,
}

/// <summary>A queued request to run a fix for an issue in a linked repo. <c>IssueId</c> is the internal
/// issue id; <c>BaseBranch</c> is the branch the draft PR targets (the project's linked base). The
/// request-side (control-plane) resolves the public UUID + repo + base branch before enqueuing.</summary>
public sealed record FixJob(long IssueId, string RepoFullName, string BaseBranch, string Actor, string Model)
{
    /// <summary>The scoped, double-scrubbed context prompt assembled by RequestFix (#63,
    /// <see cref="FixContextAssembler"/>). Empty falls back to a minimal prompt in the orchestrator.</summary>
    public string Prompt { get; init; } = "";

    /// <summary>The repo-relative files the fix should focus on (from the same assembly).</summary>
    public IReadOnlyList<string> ScopedPaths { get; init; } = [];

    /// <summary>The org's GitHub App installation, so the provider can mint a repo-scoped token.
    /// Zero when the org has no installation; providers that need GitHub then fail the run.</summary>
    public long InstallationId { get; init; }

    /// <summary>The org whose AI-fix allowance the run was reserved from (#100).</summary>
    public long OrgId { get; init; }

    /// <summary>Minted once where the run is reserved, so a job delivered twice meets its own row instead of
    /// running twice on one reservation. Empty on a job queued before this existed.</summary>
    public Guid JobId { get; init; }

    /// <summary>When the allowance was reserved, which is the month a refund returns the run to. Default on a
    /// job queued before this existed, when the run's own creation time stands in.</summary>
    public DateTimeOffset ReservedAt { get; init; }

    /// <summary>The pending run this job stands for, written by its request before the job is published,
    /// or by the orchestrator for a job that arrives with none to claim. An empty provider means none has
    /// been chosen yet.</summary>
    public FixSuggestion ToPendingRun(string provider, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), IssueId, RepoFullName, FixStatus.Pending, provider, Model,
            Branch: "", PrUrl: "", Summary: "", now, now)
        {
            JobId = JobId == Guid.Empty ? null : JobId,
            ReservedAt = ReservedAt == default ? null : ReservedAt,
        };
}

/// <summary>A Conductor fix run and its outcome (named for the <c>condux.fix.v1</c>
/// <c>FixSuggestion</c> message).</summary>
public sealed record FixSuggestion(
    Guid Id,
    long IssueId,
    string RepoFullName,
    FixStatus Status,
    string Provider,
    string Model,
    string Branch,
    string PrUrl,
    string Summary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>When the draft PR merged (from the GitHub webhook), starting the verification
    /// window (ADR-0019). Null until the PR merges.</summary>
    public DateTimeOffset? MergedAt { get; init; }

    /// <summary>Why the run failed; null unless it did, and for runs that failed before reasons were
    /// recorded. The error text is not part of the run: operators read it from the audit trail or the
    /// log, never from the API.</summary>
    public FixFailureReason? FailureReason { get; init; }

    /// <summary>Post-merge verification outcome; <see cref="VerifyStatus.None"/> until the PR merges.</summary>
    public VerifyStatus VerifyStatus { get; init; }

    /// <summary>When verification concluded (held or did not hold).</summary>
    public DateTimeOffset? VerifiedAt { get; init; }

    /// <summary>Model tokens the run consumed (0 for the no-model backends). Persisted on the row from
    /// the provider result so per-project/model cost is a plain aggregate (#120), priced via
    /// <see cref="Condux.Core.Plans.ModelPricing"/>; the counts also ride the draft_pr_opened audit.</summary>
    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    /// <summary>The request this run came from (<see cref="FixJob.JobId"/>); null when it carried none. Kept
    /// out of the API, like <see cref="ReservedAt"/>: both are the allowance's bookkeeping, not the run's.</summary>
    [JsonIgnore]
    public Guid? JobId { get; init; }

    /// <summary>When the request reserved the allowance; null when it carried none.</summary>
    [JsonIgnore]
    public DateTimeOffset? ReservedAt { get; init; }
}

/// <summary>Persists fix runs and their audit trail. Postgres-backed in production; an in-memory
/// fake backs unit tests.</summary>
public interface IFixStore
{
    /// <summary>Records a new run. False when nothing was recorded, so the caller must not run it: a run for
    /// the same <see cref="FixSuggestion.JobId"/> exists, which is a job delivered a second time, or another
    /// run is in progress on the issue, in which case the job's reservation is kept, since nothing records
    /// the refusal and a second delivery would refund it again. The orchestrator calls this whenever there
    /// is no pending run to claim (<see cref="TryClaimAsync"/>): for a job published before requests wrote
    /// their run this writes it, and for any job whose run exists, delivered again or concluded by the
    /// sweep or by a failed enqueue, the job id refuses it.</summary>
    Task<bool> InsertAsync(FixSuggestion suggestion, CancellationToken cancellationToken = default);

    /// <summary>Writes a hosted request's pending run, before its job is published, so a second request
    /// for the issue is refused while the first waits in the queue. False when another run is in progress
    /// on the issue: nothing is written, and the caller hands its reservation back.</summary>
    Task<bool> TryQueueAsync(FixSuggestion suggestion, CancellationToken cancellationToken = default);

    /// <summary>Settles a run whose queueing failed by making it cancelled: the pending run if it was written,
    /// or the run written already cancelled if it was not, which a write still in flight then cannot
    /// overtake. False when the run was already taken: claimed by the conductor, leased by a runner, or
    /// concluded. The row and its job id stay, so a job that arrives after all meets its own run and is
    /// neither run nor refunded.</summary>
    Task<bool> TryCancelQueuedAsync(FixSuggestion suggestion, CancellationToken cancellationToken = default);

    /// <summary>Marks the pending run its request wrote for <see cref="FixSuggestion.JobId"/> as running,
    /// with the provider and model that will run it, and returns its id. Null when there is no such pending
    /// run: the request wrote none, or the stale-run sweep failed and refunded it before the job arrived,
    /// in which case the job must not run.</summary>
    Task<Guid?> TryClaimAsync(FixSuggestion suggestion, CancellationToken cancellationToken = default);

    /// <summary>Marks a pending run as running. False when the run is no longer pending, and the caller must
    /// then not run it. An admin's cancel or the stale-run sweep ended it and refunded it, so running it would
    /// spend an allowance handed back; or its row was deleted with its issue, which keeps the reservation, as
    /// any run deleted in flight does.</summary>
    Task<bool> MarkRunningAsync(FixSuggestion suggestion, CancellationToken cancellationToken = default);

    /// <summary>Records the run as failed and hands its reservation back, in one transaction, but only while
    /// the run is still pending or running. The refund goes to the month the run was reserved in. False when
    /// the run was not in progress: the stale-run sweep concluded and refunded it, or its row was deleted with
    /// its issue (a project delete cascades that far). Nothing is refunded then, since a deleted row cannot say
    /// whether it was refunded already, and refunding twice would hand out a run nobody reserved.</summary>
    Task<bool> TryFailAsync(FixSuggestion suggestion, CancellationToken cancellationToken = default);

    /// <summary>Records the run as succeeded, whatever state it was in: losing a real PR's record is worse
    /// than overwriting a failure. When the stale-run sweep had already failed and refunded it, the run is
    /// taken back from the month it was reserved in, in the same transaction, never past the plan's
    /// limit.</summary>
    Task RecordSuccessAsync(FixSuggestion suggestion, CancellationToken cancellationToken = default);

    Task AppendAuditAsync(
        Guid fixId, string actor, string eventName, string detailJson, CancellationToken cancellationToken = default);

    Task<FixSuggestion?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FixSuggestion>> ListByIssueAsync(long issueId, CancellationToken cancellationToken = default);
}
