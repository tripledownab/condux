namespace Condux.Core.Repos;

/// <summary>A code mapping proposed by <see cref="CodeMappingDeriver"/>: the same
/// <c>stackRoot → sourceRoot</c> prefix rewrite a manual <see cref="CodeMapping"/> carries, plus how many
/// stack frames supported it (higher = more confident).</summary>
public sealed record DerivedMapping(string StackRoot, string SourceRoot, int MatchCount);

/// <summary>
/// Auto-derives code mappings (#113) by matching an issue's stack-frame paths against the repository's
/// file tree. For each frame it finds the repo path that shares the longest trailing path-segment suffix,
/// then the rewrite is the prefixes that differ: e.g. frame <c>/app/dist/checkout.js</c> vs repo
/// <c>src/checkout.js</c> share <c>checkout.js</c>, so the rule is <c>/app/dist/ → src/</c> (exactly what
/// <see cref="CodeMapper"/> consumes). Rules are aggregated across frames and ranked by support, so a
/// spurious single-file match ranks below the real project-wide prefix. Pure: paths in, proposals out.
/// The caller passes the in-app frame paths (library frames are not in the repo) and the repo file list.
/// </summary>
public static class CodeMappingDeriver
{
    public static IReadOnlyList<DerivedMapping> Derive(
        IEnumerable<string> stackPaths, IReadOnlyCollection<string> repoPaths)
    {
        var repoSegments = repoPaths
            .Select(Normalize)
            .Where(path => path.Length > 0)
            .Select(path => (Path: path, Segments: path.Split('/')))
            .ToList();

        var support = new Dictionary<(string Stack, string Source), int>();
        foreach (var raw in stackPaths)
        {
            if (BestMatch(Normalize(raw), repoSegments) is { } rule)
            {
                support[rule] = support.GetValueOrDefault(rule) + 1;
            }
        }

        return support
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key.Stack, StringComparer.Ordinal)
            .Select(entry => new DerivedMapping(entry.Key.Stack, entry.Key.Source, entry.Value))
            .ToList();
    }

    // The rewrite (stackRoot, sourceRoot) for the repo path sharing the longest trailing-segment suffix
    // with the frame, or null when nothing matches or the paths are already identical (no mapping needed).
    private static (string Stack, string Source)? BestMatch(
        string stackPath, IReadOnlyList<(string Path, string[] Segments)> repos)
    {
        if (stackPath.Length == 0)
        {
            return null;
        }
        var stackSegments = stackPath.Split('/');

        var bestDepth = 0;
        (string Stack, string Source)? best = null;
        foreach (var (repoPath, repoSegments) in repos)
        {
            var depth = TrailingMatch(stackSegments, repoSegments);
            if (depth == 0)
            {
                continue;
            }
            // The shared suffix is the last `depth` segments; the roots are the differing prefixes. Slice
            // the raw strings (not the segment arrays) so the roots keep their exact separators, which is
            // what CodeMapper's prefix replace needs.
            var suffix = string.Join('/', stackSegments[^depth..]);
            var stackRoot = stackPath[..^suffix.Length];
            var sourceRoot = repoPath[..^suffix.Length];
            if (stackRoot == sourceRoot)
            {
                continue; // already repo-relative for this frame; nothing to rewrite
            }
            // Prefer the deepest (most specific) match; break ties on the shorter, then lexicographically
            // smaller, source root so the result is deterministic.
            if (depth > bestDepth
                || (depth == bestDepth && best is { } current && Prefer(sourceRoot, current.Source)))
            {
                bestDepth = depth;
                best = (stackRoot, sourceRoot);
            }
        }
        return best;
    }

    private static bool Prefer(string candidate, string current) =>
        candidate.Length != current.Length
            ? candidate.Length < current.Length
            : string.CompareOrdinal(candidate, current) < 0;

    private static int TrailingMatch(string[] a, string[] b)
    {
        var max = Math.Min(a.Length, b.Length);
        var depth = 0;
        while (depth < max && a[^(depth + 1)] == b[^(depth + 1)])
        {
            depth++;
        }
        return depth;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
}
