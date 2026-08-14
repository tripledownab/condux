namespace Condux.Core.SourceControl;

/// <summary>
/// Mints a short-lived, repo-scoped access token for a source host (#145), so the fix gateway and endpoints
/// depend on the seam rather than a provider's token plumbing. GitHub's implementation exchanges the app JWT
/// for an installation token; a GitLab/Bitbucket implementation would resolve its own stored credential. The
/// <paramref name="installationId"/> key is GitHub-shaped for now — it is generalized to a repo/org identity
/// when per-provider selection lands (a later #145 slice).
/// </summary>
public interface ISourceHostTokens
{
    /// <summary>A valid access token for the given installation, minted or served from cache.</summary>
    Task<string> GetAsync(long installationId, CancellationToken ct = default);

    /// <summary>
    /// Whether minting needs a real installation id. True for the GitHub App (no installation means no
    /// token, so a zero id is refused up front with a message about connecting GitHub); false for a
    /// source that holds its own credential — a customer's runner brings its own token and has no
    /// installation at all, which is not an error but the design.
    /// </summary>
    bool RequiresInstallation => true;

    /// <summary>
    /// A token downscoped to READ-ONLY on exactly one repository, for handing to an execution
    /// environment outside our boundary (the Managed Agents sandbox mount, ADR-0038) — it must be able
    /// to clone and nothing else. Default throws: a host that cannot downscope must fail loudly here
    /// rather than silently hand its full-permission token to a vendor.
    /// </summary>
    Task<string> GetReadOnlyAsync(long installationId, string repoFullName, CancellationToken ct = default) =>
        throw new NotSupportedException(
            $"{GetType().Name} cannot mint a read-only repo-scoped token, which the managed-agents backend requires.");
}
