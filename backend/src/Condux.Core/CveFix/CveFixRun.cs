using Condux.Core.FixEngine;

namespace Condux.Core.CveFix;

/// <summary>
/// A Conductor run that opens a draft PR bumping a vulnerable dependency to its first patched version
/// (#117). A separate domain from the issue-keyed <see cref="FixSuggestion"/>: a CVE bump is keyed on
/// the linked repo + the advisory (<see cref="GhsaId"/>), reuses the same Conductor provider + the same
/// AI-fix allowance, but does not take part in the issue-occurrence verification loop (ADR-0019) — a
/// bump is reviewed and merged by a human like any draft PR. The run lifecycle reuses the shared
/// <see cref="FixStatus"/> (a generic pending→running→succeeded/failed run state).
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
}

/// <summary>Persists CVE-fix runs. Postgres-backed in production; an in-memory fake backs unit tests.</summary>
public interface ICveFixStore
{
    Task InsertAsync(CveFixRun run, CancellationToken cancellationToken = default);

    Task UpdateAsync(CveFixRun run, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CveFixRun>> ListByRepoAsync(Guid repoLinkId, CancellationToken cancellationToken = default);

    /// <summary>True when a pending/running run already exists for this repo + advisory — the guard that
    /// stops a double-click from opening two PRs (and burning two allowance slots) for the same CVE.</summary>
    Task<bool> HasActiveRunAsync(Guid repoLinkId, string ghsaId, CancellationToken cancellationToken = default);
}
