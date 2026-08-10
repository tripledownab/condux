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
}
