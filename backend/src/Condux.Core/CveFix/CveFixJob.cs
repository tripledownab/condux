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

    /// <summary>The org whose AI-fix allowance the run was reserved from, so a failed run can be
    /// refunded — mirrors <see cref="FixEngine.FixJob.OrgId"/>. Zero skips the refund.</summary>
    public long OrgId { get; init; }
}

/// <summary>Publishes a CVE-fix request onto the queue the Conductor's CVE worker drains. Kafka-backed
/// in production; a fake captures the job in tests.</summary>
public interface ICveFixPublisher
{
    Task PublishAsync(CveFixJob job, CancellationToken cancellationToken = default);
}
