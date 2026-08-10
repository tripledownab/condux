using Condux.Core.FixEngine;

namespace Condux.Core.CveFix;

/// <summary>
/// The candidate dependency-manifest paths per package ecosystem (GitHub's Dependabot ecosystem
/// strings). A CVE bump edits the manifest (and lockfile) rather than application code, so these are the
/// scoped files the Conductor fetches — it uses the ones that exist and ignores the rest. Repo-root
/// only for this slice (a manifest in a sub-directory of a monorepo is a follow-up). An unknown
/// ecosystem returns none, which the caller treats as "not supported yet" rather than burning a run.
/// </summary>
public static class EcosystemManifests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Candidates =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["npm"] = ["package.json", "package-lock.json", "pnpm-lock.yaml", "yarn.lock"],
            ["pip"] = ["requirements.txt", "pyproject.toml", "poetry.lock", "Pipfile", "Pipfile.lock"],
            ["maven"] = ["pom.xml"],
            ["gradle"] = ["build.gradle", "build.gradle.kts"],
            ["composer"] = ["composer.json", "composer.lock"],
            ["rubygems"] = ["Gemfile", "Gemfile.lock"],
            ["go"] = ["go.mod", "go.sum"],
            ["rust"] = ["Cargo.toml", "Cargo.lock"],
            ["nuget"] = ["Directory.Packages.props", "packages.config"],
            ["pub"] = ["pubspec.yaml", "pubspec.lock"],
        };

    /// <summary>The manifest candidates for an ecosystem, or an empty list when it is not supported.</summary>
    public static IReadOnlyList<string> For(string ecosystem) =>
        Candidates.TryGetValue(ecosystem, out var paths) ? paths : [];
}

/// <summary>
/// Assembles the scoped bump instruction for a CVE-fix run from the authoritative advisory fields.
/// Pure and side-effect-free (the parallel of <see cref="FixContextAssembler"/> for the issue path):
/// the control-plane builds this from the GitHub-fetched advisory before enqueuing, and the Conductor
/// feeds the prompt + scoped manifests to the same provider.
/// </summary>
public static class CveFixContextAssembler
{
    /// <summary>Build the bump prompt + the ecosystem's manifest paths. Returns an empty
    /// <see cref="FixContext.ScopedPaths"/> for an unsupported ecosystem (the caller then declines).</summary>
    public static FixContext Assemble(
        string package, string ecosystem, string fromRange, string toVersion,
        string ghsaId, string? cveId, string summary)
    {
        var advisory = string.IsNullOrEmpty(cveId) ? ghsaId : cveId;
        var prompt =
            $"Security update: bump the dependency \"{package}\" ({ecosystem}) from the vulnerable range "
            + $"\"{fromRange}\" to version \"{toVersion}\" to remediate {advisory}.\n"
            + (string.IsNullOrWhiteSpace(summary) ? "" : $"Advisory: {summary}\n")
            + "Update the dependency manifest (and its lockfile if one is present) so the resolved "
            + $"version of \"{package}\" is \"{toVersion}\" or a compatible patched version. Change only "
            + "what the bump requires; do not modify application code unless the new version needs it.";

        return new FixContext(prompt, EcosystemManifests.For(ecosystem));
    }
}
