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
            ["composer"] = ["composer.json", "composer.lock"],
            ["rubygems"] = ["Gemfile", "Gemfile.lock"],
            ["go"] = ["go.mod", "go.sum"],
            ["rust"] = ["Cargo.toml", "Cargo.lock"],
            ["nuget"] = ["Directory.Packages.props", "packages.config"],
            ["pub"] = ["pubspec.yaml", "pubspec.lock"],
        };

    // Where a Gradle build declares a version: the version catalog, the root build script in either DSL,
    // and the module build script of the Android default layout. Most likely first, since the gateway
    // reads only the first few scoped paths and an absent file still costs one.
    private static readonly IReadOnlyList<string> GradleCandidates =
    [
        "gradle/libs.versions.toml", "build.gradle.kts", "build.gradle", "app/build.gradle.kts",
        "app/build.gradle",
    ];

    /// <summary>The manifest candidates for an ecosystem, or an empty list when it is not supported.</summary>
    public static IReadOnlyList<string> For(string ecosystem) =>
        Candidates.TryGetValue(ecosystem, out var paths) ? paths : [];

    /// <summary>The candidates beside a manifest the alert names. A Gradle build file gets Gradle's, whatever
    /// the ecosystem says: GitHub reads no Gradle file itself, so a Gradle alert comes from dependency
    /// submission, which reports the ecosystem as maven and the manifest as the settings file, and maven's
    /// candidate is a pom.xml such a build does not have.</summary>
    public static IReadOnlyList<string> ForManifest(string ecosystem, string manifestPath) =>
        IsGradleBuildFile(manifestPath) ? GradleCandidates : For(ecosystem);

    /// <summary>Whether the path is a Gradle settings or build script, in either DSL.</summary>
    public static bool IsGradleBuildFile(string path) =>
        FileName(path) is "settings.gradle" or "settings.gradle.kts" or "build.gradle" or "build.gradle.kts";

    /// <summary>Whether the path is a Gradle settings script, which names the build's modules and rarely
    /// declares a version (only through an inline version catalog).</summary>
    public static bool IsGradleSettingsFile(string path) =>
        FileName(path) is "settings.gradle" or "settings.gradle.kts";

    private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];
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
    // Every system prompt asks for a test that fails without the change, which is right for a code fix
    // and has no answer for a version bump, so a model told both invented a script that greps the build
    // file. Said here, in the task, rather than by loosening the system prompts, which an issue fix still
    // needs: there is one system prompt per backend and strategy, the task is the one text they all
    // receive, and it is ours rather than the advisory's.
    // A lockfile is rewritten whole, and one the model could not read in full (cut short in the prompt, or
    // too large for the source host to return) cannot be, so a bump that tried failed outright. Leaving it
    // for the reviewer to regenerate still lands the manifest change, and the pull request says which
    // lockfile was left. Worded for both backends: one is shown files, the other reads a mounted checkout.
    private const string LockfileInstruction =
        "Update its lockfile too, but only if you could read the whole of it. A lockfile you could not read "
        + "in full, because it was cut short or was too large to read, must be left exactly as it is: do not "
        + "write it, and do not create a new one in its place. Say in the summary that the lockfile was left "
        + "for the reviewer to regenerate. ";

    private const string NoTestInstruction =
        "This is a dependency update, not a code fix: do not add tests, scripts or any other new file, "
        + "even though your general instructions ask for a test, because a version bump has none to add. "
        + "Say in the summary that no test was added for that reason.";

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
            + (manifests.Any(EcosystemManifests.IsGradleBuildFile)
                ? "This is a Gradle build: set the version where the build declares it, in the "
                  + "gradle/libs.versions.toml version catalog or a build.gradle(.kts) file, not in the "
                  + "settings file.\n"
                : "")
            + "Update the dependency manifest so the resolved "
            + $"version of \"{promptPackage}\" is \"{promptToVersion}\" or a compatible patched version. "
            + LockfileInstruction
            + "Change only what the bump requires; do not modify application code unless the new version "
            + "needs it. "
            + NoTestInstruction;

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
    // member's real lockfile out of the run entirely. A Gradle settings file is the exception and goes
    // last: it is what the alert names, yet it rarely declares a version, so leading with it would spend a
    // slot the version catalog needs; for a root build it still lands inside the in-process gateway's
    // six-file read. A known manifest with an unmapped ecosystem still scopes to that manifest alone: the
    // file to edit is known, so "ecosystem not in the table" is no reason to decline the fix.
    private static IReadOnlyList<string> ScopedPaths(string ecosystem, IReadOnlyList<string> manifests)
    {
        if (manifests.Count == 0)
        {
            return EcosystemManifests.For(ecosystem);
        }

        var siblings = manifests
            .Select(manifest => (
                Directory: CandidateRoot(manifest),
                Candidates: EcosystemManifests.ForManifest(ecosystem, manifest)))
            .ToList();
        var settings = manifests.Where(EcosystemManifests.IsGradleSettingsFile).ToList();
        var seen = new HashSet<string>(manifests, StringComparer.Ordinal);
        var paths = manifests.Except(settings).ToList();
        for (var i = 0; i < siblings.Max(sibling => sibling.Candidates.Count); i++)
        {
            foreach (var (directory, candidates) in siblings)
            {
                if (i < candidates.Count && seen.Add(directory + candidates[i]))
                {
                    paths.Add(directory + candidates[i]);
                }
            }
        }
        paths.AddRange(settings);
        return paths;
    }

    // Where a manifest's candidates are looked for: its own directory, except for a Gradle module script.
    // A settings file marks the build's root, but a module's build.gradle(.kts) does not say where that root
    // is, and its own directory would yield paths such as app/app/build.gradle, so the repository root is
    // the better guess.
    private static string CandidateRoot(string manifest) =>
        EcosystemManifests.IsGradleBuildFile(manifest) && !EcosystemManifests.IsGradleSettingsFile(manifest)
            ? ""
            : manifest.LastIndexOf('/') is var slash && slash < 0 ? "" : manifest[..(slash + 1)];
}
