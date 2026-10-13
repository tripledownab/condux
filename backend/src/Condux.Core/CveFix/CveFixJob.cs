using Condux.Core.FixEngine;

namespace Condux.Core.CveFix;

/// <summary>
/// A queued request to bump a vulnerable dependency in a linked repo. The control-plane resolves the
/// repo + the authoritative advisory (re-fetched from GitHub, never trusted from the client) and
/// assembles the scoped bump <see cref="Prompt"/> + manifest <see cref="ScopedPaths"/> before enqueuing;
/// the Conductor's <c>CveFixWorker</c> drains this off its own topic and runs it through the
/// <see cref="CveFixOrchestrator"/>.
/// </summary>
public sealed record CveFixJob(
    Guid RepoLinkId,
    string RepoFullName,
    string BaseBranch,
    string GhsaId,
    string? CveId,
    string Package,
    string Ecosystem,
    string FromRange,
    string ToVersion,
    string AdvisoryUrl,
    string Actor,
    string Model)
{
    /// <summary>The bump instruction assembled by <see cref="CveFixContextAssembler"/>.</summary>
    public string Prompt { get; init; } = "";

    /// <summary>The candidate dependency-manifest paths for the ecosystem (the provider fetches those
    /// that exist and edits them); never the whole repo.</summary>
    public IReadOnlyList<string> ScopedPaths { get; init; } = [];

    /// <summary>The org's GitHub App installation, so the provider can mint a repo-scoped token.
    /// Zero when the org has no installation; providers that need GitHub then fail the run.</summary>
    public long InstallationId { get; init; }

    /// <summary>The org whose AI-fix allowance the run was reserved from.</summary>
    public long OrgId { get; init; }

    /// <summary>Minted once where the run is reserved, so a job delivered twice meets its own row instead of
    /// running twice on one reservation. Empty on a job queued before this existed.</summary>
    public Guid JobId { get; init; }

    /// <summary>When the allowance was reserved, which is the month a refund returns the run to. Default on a
    /// job queued before this existed, when the run's own creation time stands in.</summary>
    public DateTimeOffset ReservedAt { get; init; }

    /// <summary>
    /// Every vulnerable range the advisory gives the package, <see cref="FromRange"/> first, which a
    /// merged bump is verified against (ADR-0051). Empty on a job queued before this existed.
    /// </summary>
    public IReadOnlyList<string> VulnerableRanges { get; init; } = [];

    /// <summary>The pending run this job stands for, written by its request before the job is published,
    /// or by the orchestrator for a job that arrives with none to claim. An empty provider means none has
    /// been chosen yet.</summary>
    public CveFixRun ToPendingRun(string provider, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), RepoLinkId, GhsaId, CveId, Package, Ecosystem, FromRange, ToVersion,
            AdvisoryUrl, FixStatus.Pending, provider, Model, Branch: "", PrUrl: "", Summary: "", Actor,
            now, now)
        {
            JobId = JobId == Guid.Empty ? null : JobId,
            ReservedAt = ReservedAt == default ? null : ReservedAt,
            // A job whose JSON carries the field as null reads as one that carries none.
            VulnerableRanges = VulnerableRanges is { Count: > 0 } ? VulnerableRanges : null,
        };
}

/// <summary>Publishes a CVE-fix request onto the queue the Conductor's CVE worker drains. Kafka-backed
/// in production; a fake captures the job in tests.</summary>
public interface ICveFixPublisher
{
    Task PublishAsync(CveFixJob job, CancellationToken cancellationToken = default);
}
