using System.Text.Json.Serialization;
using Condux.Core.FixEngine;

namespace Condux.Core.CveFix;

/// <summary>
/// A Conductor run that opens a draft PR bumping a vulnerable dependency to its first patched version
/// (#117). A separate domain from the issue-keyed <see cref="FixSuggestion"/>: a CVE bump is keyed on
/// the linked repo + the advisory (<see cref="GhsaId"/>), reuses the same Conductor provider + the same
/// AI-fix allowance, and is reviewed and merged by a human like any draft PR. It has no issue for the
/// ADR-0019 loop to watch; once merged it is verified against the runtime inventory instead (ADR-0051).
/// The run lifecycle reuses the shared <see cref="FixStatus"/> (a generic pending→running→succeeded/failed
/// run state).
/// </summary>
public sealed record CveFixRun(
    Guid Id,
    Guid RepoLinkId,
    string GhsaId,
    string? CveId,
    string Package,
    string Ecosystem,
    string FromRange,
    string ToVersion,
    string AdvisoryUrl,
    FixStatus Status,
    string Provider,
    string Model,
    string Branch,
    string PrUrl,
    string Summary,
    string Actor,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>Model tokens the run consumed (0 for the no-model backends), persisted on the row so
    /// CVE-bump spend is a plain aggregate priced via <see cref="Condux.Core.Plans.ModelPricing"/>,
    /// consistent with the issue-fix cost model (#120).</summary>
    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    /// <summary>Why the run failed; null unless it did, and for runs that failed before reasons were
    /// recorded. The error text is not part of the run: operators read it from the audit trail or the
    /// log, never from the API.</summary>
    public FixFailureReason? FailureReason { get; init; }

    /// <summary>The request this run came from (<see cref="CveFixJob.JobId"/>); null when it carried none. Kept
    /// out of the API, like <see cref="ReservedAt"/>: both are the allowance's bookkeeping, not the run's.</summary>
    [JsonIgnore]
    public Guid? JobId { get; init; }

    /// <summary>When the request reserved the allowance; null when it carried none.</summary>
    [JsonIgnore]
    public DateTimeOffset? ReservedAt { get; init; }

    /// <summary>When the bump's draft PR merged, which starts its verification (ADR-0051); null until then.</summary>
    public DateTimeOffset? MergedAt { get; init; }

    /// <summary>Where the post-merge verification stands: None until the merge, then Watching, then Held,
    /// DidNotHold (still exposed) or CannotVerify.</summary>
    public VerifyStatus VerifyStatus { get; init; }

    /// <summary>When the verification concluded; null while it has not.</summary>
    public DateTimeOffset? VerifiedAt { get; init; }

    /// <summary>What a concluded verification rested on: every sighting since the merge and how each was
    /// read. Null while it has not concluded.</summary>
    public CveFixEvidence? VerifyEvidence { get; init; }

    /// <summary>Every vulnerable range the advisory gives the package, stored at request time for the
    /// post-merge verification (ADR-0051); null on a run requested before that.</summary>
    [JsonIgnore]
    public IReadOnlyList<string>? VulnerableRanges { get; init; }
}

/// <summary>Persists CVE-fix runs. Postgres-backed in production; an in-memory fake backs unit tests.</summary>
public interface ICveFixStore
{
    /// <summary>Records a new run. False when nothing was recorded, and the caller must not run it: either a
    /// run for the same <see cref="CveFixRun.JobId"/> exists, which is a job delivered a second time, or another
    /// job's run is in progress on the advisory, in which case this job's reservation is handed back to the
    /// monthly allowance in the same transaction, once per job, and the job is never run later. A job with no
    /// id is not refunded, since it cannot be told from a delivery again. The orchestrator calls this whenever
    /// there is no pending run to claim (<see cref="TryClaimAsync"/>): for a job published before requests
    /// wrote their run this writes it, and for any job whose run exists, delivered again or concluded by the
    /// sweep or by a failed enqueue, the job id refuses it without a refund.</summary>
    Task<bool> InsertAsync(CveFixRun run, CancellationToken cancellationToken = default);

    /// <summary>Writes a hosted request's pending run, before its job is published, so a second request
    /// for the advisory is refused while the first waits in the queue. False when another run is in
    /// progress on the advisory: nothing is written, and the caller hands its reservation back.</summary>
    Task<bool> TryQueueAsync(CveFixRun run, CancellationToken cancellationToken = default);

    /// <summary>Settles a run whose queueing failed by making it cancelled: the pending run if it was written,
    /// or the run written already cancelled if it was not, which a write still in flight then cannot
    /// overtake. False when the run was already taken: claimed by the conductor, leased by a runner, or
    /// concluded. The row and its job id stay, so a job that arrives after all meets its own run and is
    /// neither run nor refunded.</summary>
    Task<bool> TryCancelQueuedAsync(CveFixRun run, CancellationToken cancellationToken = default);

    /// <summary>Marks the pending run its request wrote for <see cref="CveFixRun.JobId"/> as running, with
    /// the provider and model that will run it, and returns its id. Null when there is no such pending run:
    /// the request wrote none, or the stale-run sweep failed and refunded it before the job arrived, in
    /// which case the job must not run.</summary>
    Task<Guid?> TryClaimAsync(CveFixRun run, CancellationToken cancellationToken = default);

    /// <summary>Marks a pending run as running. False when the run is no longer pending, and the caller must
    /// then not run it. An admin's cancel or the stale-run sweep ended it and refunded it, so running it would
    /// spend an allowance handed back; or its row was deleted with its linked repository, which keeps the reservation, as
    /// any run deleted in flight does.</summary>
    Task<bool> MarkRunningAsync(CveFixRun run, CancellationToken cancellationToken = default);

    /// <summary>Records the run as failed and hands its reservation back, in one transaction, but only while
    /// the run is still pending or running. The refund goes to the month the run was reserved in. False when
    /// the run was not in progress: the stale-run sweep concluded and refunded it, or its row was deleted with
    /// its linked repository. Nothing is refunded then, since a deleted row cannot say whether it was refunded
    /// already, and refunding twice would hand out a run nobody reserved.</summary>
    Task<bool> TryFailAsync(CveFixRun run, CancellationToken cancellationToken = default);

    /// <summary>Records the run as succeeded, whatever state it was in: losing a real PR's record is worse
    /// than overwriting a failure. When the stale-run sweep had already failed and refunded it, the run is
    /// taken back from the month it was reserved in, in the same transaction, never past the plan's
    /// limit.</summary>
    Task RecordSuccessAsync(CveFixRun run, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CveFixRun>> ListByRepoAsync(Guid repoLinkId, CancellationToken cancellationToken = default);

    /// <summary>True when a pending/running run already exists for this repo + advisory — the guard that
    /// stops a double-click from opening two PRs (and burning two allowance slots) for the same CVE.</summary>
    Task<bool> HasActiveRunAsync(Guid repoLinkId, string ghsaId, CancellationToken cancellationToken = default);
}
