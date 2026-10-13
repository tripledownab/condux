using Condux.GitHub;

namespace Condux.ControlPlane.Endpoints;

/// <summary>
/// The authoritative advisory behind a CVE bump, re-read from GitHub. The client supplies only the GHSA id,
/// so a stale or forged package or version can never drive the bump.
/// </summary>
internal static class DependabotAdvisories
{
    /// <summary>The repository's open alerts for the advisory, the fixable one (it names the patched version)
    /// first, or null when GitHub could not be read. One advisory can raise an alert per monorepo workspace
    /// member (#41), and every match is kept, since the run bumps them all in one draft PR.</summary>
    public static async Task<IReadOnlyList<DependabotAlert>?> FetchMatchingAsync(
        GitHubInstallationTokens tokens, GitHubRepoClient repoClient, long installationId, string repoFullName,
        string ghsaId, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var token = await tokens.GetAsync(installationId, cancellationToken);
            return
            [
                .. (await repoClient.ListDependabotAlertsAsync(token, repoFullName, cancellationToken)).Alerts
                    .Where(a => a.GhsaId == ghsaId)
                    .OrderByDescending(a => !string.IsNullOrEmpty(a.FixedVersion)),
            ];
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "cve-fix advisory fetch failed repo={Repo}", repoFullName);
            return null;
        }
    }
}
