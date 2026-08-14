namespace Condux.Core.CveScanning;

/// <summary>
/// Which files a dependency scanner needs from a repository. A lookup rather than a chain of checks, so
/// supporting another ecosystem is one line here and nothing else.
///
/// Manifests are deliberately excluded where a lockfile exists: a manifest states a range, a lockfile
/// states what is actually installed, and scanning the range reports vulnerabilities in versions nobody
/// is running. Where an ecosystem has no lockfile in common use, the manifest is all there is.
/// </summary>
public static class Lockfiles
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        // JavaScript
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml",
        // Python. requirements.txt is a manifest by convention but is what most projects pin in.
        "poetry.lock", "Pipfile.lock", "requirements.txt", "uv.lock",
        // Go, Rust, Ruby, PHP
        "go.mod", "Cargo.lock", "Gemfile.lock", "composer.lock",
        // JVM and .NET, which pin in a manifest rather than a lockfile.
        "pom.xml", "gradle.lockfile", "packages.lock.json",
    };

    /// <summary>
    /// How many lockfiles one scan will read. A monorepo can hold hundreds, and every one is a round trip
    /// to the forge before the scan even starts; the cap bounds that rather than letting a large repo
    /// quietly turn a page load into a minute.
    /// </summary>
    public const int MaxPerScan = 40;

    public static bool IsLockfile(string path) => Names.Contains(FileName(path));

    /// <summary>
    /// The lockfiles in a repository's file listing, shallowest first and capped. Shallowest first because
    /// a repository's own lockfiles sit near the root while vendored and fixture copies sit deep, so a
    /// truncated scan should keep the ones that matter.
    /// </summary>
    public static IReadOnlyList<string> SelectFrom(IEnumerable<string> repoPaths) =>
    [
        .. repoPaths
            .Where(IsLockfile)
            .Where(path => !IsVendored(path))
            .OrderBy(path => path.Count(c => c == '/'))
            .ThenBy(path => path, StringComparer.Ordinal)
            .Take(MaxPerScan),
    ];

    /// <summary>
    /// Dependencies that are not the project's own. A vendored copy or a test fixture reports real
    /// vulnerabilities that nobody ships, which is the fastest way to make a security surface ignorable.
    /// </summary>
    private static bool IsVendored(string path) =>
        path.Contains("node_modules/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("vendor/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("third_party/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("testdata/", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/fixtures/", StringComparison.OrdinalIgnoreCase);

    private static string FileName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }
}
