using Condux.Core.FixEngine;

namespace Condux.Core.CveFix;

/// <summary>
/// The candidate dependency-manifest paths per package ecosystem (GitHub's Dependabot ecosystem
/// strings). A CVE bump edits the manifest (and lockfile) rather than application code, so these are the
/// scoped files the Conductor fetches — it uses the ones that exist and ignores the rest. These are the
/// repo-root fallback; when the alert names its own manifest path (#41, a monorepo workspace member),
/// the assembler prefixes this list with that manifest's directory instead. An unknown ecosystem returns
/// none, which the caller treats as "not supported yet" rather than burning a run — unless a manifest
/// path is known, which is enough to fix on by itself.
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
/// Pure and side-effect-free (the CVE counterpart of <see cref="FixContextAssembler"/>, which
/// serves the issue path):
/// the control-plane builds this from the GitHub-fetched advisory before enqueuing, and the Conductor
/// feeds the prompt + scoped manifests to the same provider.
/// </summary>
public static class CveFixContextAssembler
{
    /// <summary>Build the bump prompt + the manifest paths to scope to. When the alert names its
    /// manifest(s) (#41 — one advisory can affect several monorepo workspace members, all bumped in one
    /// run), the scope is those manifests plus each one's sibling lockfile candidates; otherwise the
    /// ecosystem's repo-root candidates. Returns an empty <see cref="FixContext.ScopedPaths"/> only for
    /// an unsupported ecosystem with no known manifest (the caller then declines).</summary>
    public static FixContext Assemble(
        string package, string ecosystem, string fromRange, string toVersion,
        string ghsaId, string? cveId, string summary, IReadOnlyList<string>? manifestPaths = null)
    {
        var manifests = Manifests(manifestPaths);
        var advisory = string.IsNullOrEmpty(cveId) ? ghsaId : cveId;

        // Every value that reaches the prompt comes from the advisory or the Dependabot alert, so each is
        // neutralized ONCE, here. Doing it at each use is what this looked like first, and that version
        // had already grown the defect it invites: five values were neutralized inline while the manifest
        // paths beside them were interpolated raw, and those come from the alert too
        // (GitHubRepoClient reads alert.Dependency.ManifestPath).
        //
        // These copies are for the PROMPT ONLY. ScopedPaths below must keep the real paths, or the
        // gateway fetches a filename that does not exist.
        var promptPackage = UntrustedText.Neutralize(package);
        var promptEcosystem = UntrustedText.Neutralize(ecosystem);
        var promptFromRange = UntrustedText.Neutralize(fromRange);
        var promptToVersion = UntrustedText.Neutralize(toVersion);
        var promptAdvisory = UntrustedText.Neutralize(advisory);
        var promptManifests = manifests.Select(UntrustedText.Neutralize);

        // The advisory summary is fenced rather than interpolated, because it is prose someone wrote
        // rather than a value the instruction needs to read as its task. It is the declared parallel of
        // FixContextAssembler, and applying the rule to one and not the other is how the two drift.
        var prompt =
            UntrustedText.Guidance + "\n\n"
            + $"Security update: bump the dependency \"{promptPackage}\" ({promptEcosystem}) from the "
            + $"vulnerable range \"{promptFromRange}\" to version \"{promptToVersion}\" to remediate "
            + $"{promptAdvisory}.\n"
            + (string.IsNullOrWhiteSpace(summary) ? "" : UntrustedText.Fence("advisory", summary) + "\n")
            + (manifests.Count > 0
                ? $"The vulnerable dependency is declared in: {string.Join(", ", promptManifests)}.\n"
                : "")
            + "Update the dependency manifest (and its lockfile if one is present) so the resolved "
            + $"version of \"{promptPackage}\" is \"{promptToVersion}\" or a compatible patched version. "
            + "Change only what the bump requires; do not modify application code unless the new version "
            + "needs it.";

        return new FixContext(prompt, ScopedPaths(ecosystem, manifests));
    }

    private static IReadOnlyList<string> Manifests(IReadOnlyList<string>? manifestPaths) =>
        manifestPaths is null
            ? []
            : [.. manifestPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path.Trim().TrimStart('/'))
                .Distinct(StringComparer.Ordinal)];

    // The alert's own manifests first (they exist by definition — the provider fetches what exists and
    // ignores the rest), then the sibling candidates ROUND-ROBIN across manifests rather than all of one
    // directory's before the next: the gateway fetches only the first few scoped paths, and absent
    // candidates burn slots, so ordering one member's whole candidate list first could starve the other
    // member's real lockfile out of the run entirely. A known manifest with an unmapped ecosystem still
    // scopes to that manifest alone: the file to edit is known, so "ecosystem not in the table" is no
    // reason to decline the fix.
    private static IReadOnlyList<string> ScopedPaths(string ecosystem, IReadOnlyList<string> manifests)
    {
        var candidates = EcosystemManifests.For(ecosystem);
        if (manifests.Count == 0)
        {
            return candidates;
        }

        var directories = manifests
            .Select(manifest => manifest.LastIndexOf('/') is var slash && slash < 0
                ? ""
                : manifest[..(slash + 1)])
            .ToList();
        var seen = new HashSet<string>(manifests, StringComparer.Ordinal);
        var paths = new List<string>(manifests);
        foreach (var candidate in candidates)
        {
            foreach (var directory in directories)
            {
                var path = directory + candidate;
                if (seen.Add(path))
                {
                    paths.Add(path);
                }
            }
        }
        return paths;
    }
}
