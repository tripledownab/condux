using Condux.Core.CveScanning;

namespace Condux.GitHub;

/// <summary>
/// The GitHub fast path: Dependabot has already scanned the repository, so the findings are a read rather
/// than a run. Free, instant, and no container — worth keeping as an accelerator even once the neutral
/// scanner covers every forge.
///
/// It reports the same <see cref="CveFinding"/> as any other scanner, so nothing above this knows which
/// path produced a given finding beyond the source recorded on it.
/// </summary>
public sealed class DependabotCveScanner(GitHubRepoClient repo) : ICveScanner
{
    public const string SourceName = "dependabot";

    public string Name => SourceName;

    public async Task<IReadOnlyList<CveFinding>> ScanAsync(
        string token, string repoFullName, CancellationToken cancellationToken = default)
    {
        var alerts = await repo.ListDependabotAlertsAsync(token, repoFullName, cancellationToken);

        return CveSeverities.Normalize(alerts.Select(alert => new CveFinding(
            AdvisoryId: alert.GhsaId,
            CveId: alert.CveId,
            // Dependabot reports a word, not a score, and says "moderate" where CVSS says "medium".
            Severity: CveSeverities.Parse(alert.Severity),
            Summary: alert.Summary,
            Package: alert.Package,
            Ecosystem: alert.Ecosystem,
            VulnerableRange: alert.VulnerableRange,
            FixedVersion: alert.FixedVersion,
            Url: alert.HtmlUrl,
            Source: SourceName)
        {
            ManifestPath = alert.ManifestPath,
        }));
    }
}
