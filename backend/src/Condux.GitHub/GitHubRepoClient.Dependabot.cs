using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Condux.Core.CveFix;
using Condux.Core.Http;

namespace Condux.GitHub;

/// <summary>An open Dependabot alert (CVE) on a repo (#117): the advisory ids + severity, the vulnerable
/// package and range, the first patched version to bump to (null when none is published yet), and the
/// repo-relative manifest the dependency is declared in (null when GitHub omits it), which is what lets
/// a bump target a monorepo workspace member instead of assuming the repo root (#41).</summary>
public sealed record DependabotAlert(
    string GhsaId,
    string? CveId,
    string Severity,
    string Summary,
    string Package,
    string Ecosystem,
    string VulnerableRange,
    string? FixedVersion,
    string HtmlUrl,
    string? ManifestPath = null)
{
    /// <summary>Every package and range the alert's advisory lists, this alert's own among them
    /// (<c>security_advisory.vulnerabilities</c>); null when the alert carries no such list, or one with an
    /// entry missing its package or range, which must not be read as an advisory with fewer ranges than
    /// it has.</summary>
    public IReadOnlyList<AdvisoryRange>? AdvisoryRanges { get; init; }
}

/// <summary>A repo's open Dependabot alerts, and whether the read stopped before the last page. A cap
/// nobody is told about reads as full coverage, so the flag travels with the list rather than being
/// logged and dropped.</summary>
public sealed record DependabotAlertList(IReadOnlyList<DependabotAlert> Alerts, bool Truncated);

/// <summary>
/// The Dependabot read: GitHub-specific, so it sits beside the neutral <see cref="Core.SourceControl.ISourceHostClient"/>
/// operations rather than among them. Split from the main file because it is the one read that pages.
/// </summary>
public sealed partial class GitHubRepoClient
{
    /// <summary>
    /// The most pages of alerts read for one repo, at 100 per page. It exists so a repository with a
    /// very large alert count cannot turn one page load into an unbounded walk of the GitHub API.
    /// </summary>
    public const int MaxAlertPages = 10;

    /// <summary>
    /// Every open Dependabot alert (CVE) for a repo (#117): the vulnerable package, its severity, the
    /// vulnerable range and the first patched version. Needs the App's "Dependabot alerts: read"
    /// permission; without it GitHub 403s.
    ///
    /// <para>Follows the <c>Link</c> header's next link, which is how this endpoint pages (it is
    /// cursor-based, so the next URL is the only way to ask for the next page). Reading only the first
    /// page made a repo with more than 100 open alerts show exactly 100, as if that were the total.
    /// Every way of stopping early is reported as <see cref="DependabotAlertList.Truncated"/>: the
    /// ceiling, a next link it will not follow, and a later page that fails.</para>
    /// </summary>
    public async Task<DependabotAlertList> ListDependabotAlertsAsync(
        string token, string repoFullName, CancellationToken ct = default)
    {
        var alerts = new List<DependabotAlert>();
        var path = $"/repos/{ToRepoSegment(repoFullName)}/dependabot/alerts?state=open&per_page=100";
        for (var page = 0; page < MaxAlertPages; page++)
        {
            HttpResponseMessage resp;
            try
            {
                resp = await SendAsync(token, HttpMethod.Get, path, body: null, ct);
            }
            // A later page that cannot be read leaves the earlier ones true, so they are returned and
            // marked partial. The first page has nothing to keep, so its failure is the caller's.
            catch (HttpRequestException) when (page > 0)
            {
                return new DependabotAlertList(alerts, Truncated: true);
            }
            // A timeout arrives as a cancellation, but the token was not cancelled: only that one is
            // partial. A cancelled token still means the caller has stopped waiting.
            catch (OperationCanceledException) when (page > 0 && !ct.IsCancellationRequested)
            {
                return new DependabotAlertList(alerts, Truncated: true);
            }

            using (resp)
            {
                if (page > 0 && !resp.IsSuccessStatusCode)
                {
                    return new DependabotAlertList(alerts, Truncated: true);
                }

                resp.EnsureSuccessStatusCode();
                var batch = await resp.Content.ReadFromJsonAsync<List<AlertResponse>>(ct) ?? [];
                alerts.AddRange(batch.Select(ToAlert));

                var next = resp.Headers.TryGetValues("Link", out var links) ? LinkHeaders.FindNextUrl(links) : null;
                if (next is null)
                {
                    return new DependabotAlertList(alerts, Truncated: false);
                }

                // There were more alerts, so a next link this client will not follow leaves the list
                // partial rather than ending it.
                if (ToNextPath(next) is not { } nextPath)
                {
                    return new DependabotAlertList(alerts, Truncated: true);
                }

                path = nextPath;
            }
        }

        return new DependabotAlertList(alerts, Truncated: true);
    }

    /// <summary>
    /// The path to request for a next link, or null when it must not be followed. Two rules decide.
    ///
    /// <para>The installation token goes with the request, so only a link under this client's own API
    /// base is followed. A path-absolute link is resolved against that base, as RFC 8288 section 3.1
    /// requires of a relative target; anything else, including a network-path <c>//host</c>, is
    /// refused.</para>
    ///
    /// <para>The link must still ask for open alerts. GitHub documents only that <c>per_page</c> rides
    /// along in its links, and a next link that lost <c>state=open</c> would mix fixed and dismissed
    /// alerts into a list presented as open ones.</para>
    /// </summary>
    private string? ToNextPath(string next)
    {
        var path = next.StartsWith(apiBaseUrl + "/", StringComparison.Ordinal) ? next[apiBaseUrl.Length..]
            : next.StartsWith('/') && !next.StartsWith("//", StringComparison.Ordinal) ? next
            : null;
        return path is not null && path.Contains("state=open", StringComparison.Ordinal) ? path : null;
    }

    private static DependabotAlert ToAlert(AlertResponse alert) => new(
        alert.SecurityAdvisory.GhsaId,
        alert.SecurityAdvisory.CveId,
        alert.SecurityAdvisory.Severity,
        alert.SecurityAdvisory.Summary,
        alert.SecurityVulnerability.Package.Name,
        alert.SecurityVulnerability.Package.Ecosystem,
        alert.SecurityVulnerability.VulnerableVersionRange,
        alert.SecurityVulnerability.FirstPatchedVersion?.Identifier,
        alert.HtmlUrl,
        alert.Dependency?.ManifestPath)
    {
        AdvisoryRanges = ToAdvisoryRanges(alert.SecurityAdvisory.Vulnerabilities),
    };

    // Null for no list, for an empty one (which cannot be right, since the alert's own package is one of
    // its entries), and for a list with an entry it cannot read, since dropping that entry could drop a
    // range of the bumped package itself: a bump with no ranges cannot be verified, which is the safe side.
    private static List<AdvisoryRange>? ToAdvisoryRanges(IReadOnlyList<VulnerabilityJson>? vulnerabilities) =>
        vulnerabilities is null or { Count: 0 }
        || vulnerabilities.Any(v => v.Package?.Name is null || v.Package.Ecosystem is null || v.VulnerableVersionRange is null)
            ? null
            : [.. vulnerabilities.Select(v => new AdvisoryRange(v.Package.Ecosystem, v.Package.Name, v.VulnerableVersionRange))];

    // The Dependabot alert JSON: advisory ids/severity/summary, plus the alert-specific vulnerability
    // (its package + range + first patched version). See https://docs.github.com/rest/dependabot/alerts.
    private sealed record AlertResponse(
        [property: JsonPropertyName("security_advisory")] AdvisoryJson SecurityAdvisory,
        [property: JsonPropertyName("security_vulnerability")] VulnerabilityJson SecurityVulnerability,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        // The schema marks dependency.manifest_path optional, so both levels stay nullable.
        [property: JsonPropertyName("dependency")] DependencyJson? Dependency = null);

    private sealed record DependencyJson(
        [property: JsonPropertyName("manifest_path")] string? ManifestPath);

    private sealed record AdvisoryJson(
        [property: JsonPropertyName("ghsa_id")] string GhsaId,
        [property: JsonPropertyName("cve_id")] string? CveId,
        [property: JsonPropertyName("severity")] string Severity,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("vulnerabilities")] IReadOnlyList<VulnerabilityJson>? Vulnerabilities = null);

    private sealed record VulnerabilityJson(
        [property: JsonPropertyName("package")] PackageJson Package,
        [property: JsonPropertyName("vulnerable_version_range")] string VulnerableVersionRange,
        [property: JsonPropertyName("first_patched_version")] PatchedVersionJson? FirstPatchedVersion);

    private sealed record PackageJson(
        [property: JsonPropertyName("ecosystem")] string Ecosystem,
        [property: JsonPropertyName("name")] string Name);

    private sealed record PatchedVersionJson([property: JsonPropertyName("identifier")] string Identifier);
}
