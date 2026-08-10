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

    /// <summary>The org whose AI-fix allowance the run was reserved from, so a failed run can be
    /// refunded (#100). Zero skips the refund (a job enqueued outside the quota gate).</summary>
    public long OrgId { get; init; }
}

/// <summary>A Conductor fix run and its outcome (mirrors the <c>condux.fix.v1</c> <c>FixSuggestion</c>).</summary>
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

    /// <summary>Post-merge verification outcome; <see cref="VerifyStatus.None"/> until the PR merges.</summary>
    public VerifyStatus VerifyStatus { get; init; }

    /// <summary>When verification concluded (held or did not hold).</summary>
    public DateTimeOffset? VerifiedAt { get; init; }

    /// <summary>Model tokens the run consumed (0 for the no-model backends). Persisted on the row from
    /// the provider result so per-project/model cost is a plain aggregate (#120), priced via
    /// <see cref="Condux.Core.Plans.ModelPricing"/>; the counts also ride the draft_pr_opened audit.</summary>
    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }
}

/// <summary>Persists fix runs and their audit trail. Postgres-backed in production; an in-memory
/// fake backs unit tests.</summary>
public interface IFixStore
{
    Task InsertAsync(FixSuggestion suggestion, CancellationToken cancellationToken = default);

    Task UpdateAsync(FixSuggestion suggestion, CancellationToken cancellationToken = default);

    Task AppendAuditAsync(
        Guid fixId, string actor, string eventName, string detailJson, CancellationToken cancellationToken = default);

    Task<FixSuggestion?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FixSuggestion>> ListByIssueAsync(long issueId, CancellationToken cancellationToken = default);
}
