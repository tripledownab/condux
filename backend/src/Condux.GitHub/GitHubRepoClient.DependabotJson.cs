using System.Text.Json;
using System.Text.Json.Serialization;
using Condux.Core.CveFix;

namespace Condux.GitHub;

/// <summary>
/// The Dependabot alert's wire shape and how it reads into a <see cref="DependabotAlert"/>. Shared by the
/// paged read and the <c>dependabot_alert</c> webhook, which carries the same alert object.
/// </summary>
public sealed partial class GitHubRepoClient
{
    /// <summary>An alert object outside a list, as a <c>dependabot_alert</c> webhook carries it (the same
    /// schema as the REST alert); null when it lacks the advisory id or the package.</summary>
    internal static DependabotAlert? ToDependabotAlert(JsonElement alert)
    {
        try
        {
            return alert.Deserialize<AlertResponse>() is
            {
                SecurityAdvisory.GhsaId: not null,
                SecurityVulnerability.Package: { Name: not null, Ecosystem: not null },
            } response
                ? ToAlert(response)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
