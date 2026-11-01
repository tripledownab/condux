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
///
/// A frame that already names a repo file (a Turbopack build symbolicates to repo-relative paths) supports
/// the identity rule, <c>"" → ""</c>. It changes no path, but a frame resolves only through a mapping, and
/// the issue page links a frame to GitHub only when one applies, so without it such a project could never
/// get its links from a suggestion. <see cref="CodeMapper"/> picks the longest stack root, so the empty one
/// never overrides a narrower rule beside it, and which rules are offered together is decided by
/// <see cref="CodeMappingSelection"/>, so that, saved as a whole, they send no sampled frame that matched
/// a repo file to a different one.
/// </summary>
public static class CodeMappingDeriver
{
    /// <summary>The rules the frames support whose stack root the repo does not map yet, and the outcome
    /// that explains an empty list. <paramref name="existing"/> are the repo's current mappings and
    /// <paramref name="mappedElsewhere"/> the stack roots the project's other linked repos map: a rule on
    /// one of those would tie with it, and a frame two repos claim links into neither.</summary>
    public static MappingSuggestions Suggest(
        IReadOnlyCollection<string> stackPaths, IReadOnlyCollection<string> repoPaths,
        IReadOnlyCollection<CodeMapping> existing, IReadOnlyCollection<string> mappedElsewhere)
    {
        if (stackPaths.Count == 0)
        {
            return MappingSuggestions.None(MappingSuggestions.NoFrames);
        }
        var (derived, ambiguous) = DeriveWithAmbiguity(stackPaths, repoPaths);
        if (derived.Count == 0)
        {
            return MappingSuggestions.None(ambiguous ? MappingSuggestions.Ambiguous : MappingSuggestions.NoMatch);
        }
        IReadOnlyList<CodeMapping> current = [.. existing];
        var mappedStackRoots = existing.Select(mapping => mapping.StackRoot)
            .Concat(mappedElsewhere)
            .ToHashSet(StringComparer.Ordinal);
        var fresh = derived
            .Where(rule => !IsCovered(rule, current) && !mappedStackRoots.Contains(rule.StackRoot))
            .ToList();
        if (fresh.Count > 0)
        {
            return new MappingSuggestions(fresh, MappingSuggestions.Suggested);
        }
        return MappingSuggestions.None(derived.All(rule => IsCovered(rule, current))
            ? MappingSuggestions.AlreadyMapped
            : MappingSuggestions.Conflicting);
    }

    // Whether the current mappings already rewrite this rule's stack root to its source root, so the rule
    // would change nothing: com/acme/ → src/main/java/com/acme/ beside an existing "" → src/main/java/.
    private static bool IsCovered(DerivedMapping rule, IReadOnlyList<CodeMapping> current) =>
        CodeMapper.Resolve(rule.StackRoot, current) == rule.SourceRoot;

    public static IReadOnlyList<DerivedMapping> Derive(
        IEnumerable<string> stackPaths, IReadOnlyCollection<string> repoPaths) =>
        DeriveWithAmbiguity(stackPaths, repoPaths).Rules;

    // The rules, and whether every frame matched a repo file while some tied between two by more than the
    // file name, so that the ties, not the repo, explain an empty answer. A tie on the file name alone
    // (index.js in two directories) is what a frame from another repo or branch meets too, so it counts as
    // matching nothing: it names no answer, and holding the rules to its files would let one stale frame
    // break up the project's main rule.
    private static (IReadOnlyList<DerivedMapping> Rules, bool Ambiguous) DeriveWithAmbiguity(
        IEnumerable<string> stackPaths, IReadOnlyCollection<string> repoPaths)
    {
        var repoSegments = repoPaths
            .Select(Normalize)
            .Where(path => path.Length > 0)
            // One file listed twice must not read as a tie with itself.
            .Distinct(StringComparer.Ordinal)
            .Select(path => (Path: path, Segments: path.Split('/')))
            .ToList();

        var repoFiles = repoSegments.Select(repo => repo.Path).ToHashSet(StringComparer.Ordinal);
        var framesByRule = new Dictionary<(string Stack, string Source), List<string>>();
        // Frames whose rule rewrites from an empty stack root, by source root: such a rule is narrowed
        // before it is offered (CodeMappingSelection), since saved as it is it applies to every frame.
        var unrooted = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // Frames that tied by more than the file name, with the files they tied between: they support no
        // rule, but whatever is offered must still send each to one of those files or to nothing.
        var tied = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var unmatched = false;
        foreach (var raw in stackPaths)
        {
            var stackPath = Normalize(raw);
            // A frame that already names a file in the repo supports the identity rule and nothing else.
            // Scanning on would pair it with another file of the same name (every page.tsx, route.ts,
            // index.ts) and propose a rule that points it at the wrong file.
            var (match, tie) = repoFiles.Contains(stackPath) ? (Identity, null) : BestMatch(stackPath, repoSegments);
            if (tie is { BeyondFileName: true })
            {
                tied.TryAdd(stackPath, tie.Files);
            }
            if (match is not { } rule)
            {
                unmatched |= tie is not { BeyondFileName: true };
                continue;
            }
            var unrootedRewrite = rule.Stack.Length == 0 && rule.Source.Length > 0;
            if (unrootedRewrite && !unrooted.ContainsKey(rule.Source))
            {
                unrooted[rule.Source] = [];
            }
            if (!unrootedRewrite && !framesByRule.ContainsKey(rule))
            {
                framesByRule[rule] = [];
            }
            (unrootedRewrite ? unrooted[rule.Source] : framesByRule[rule]).Add(stackPath);
        }

        var rules = CodeMappingSelection.Select(
            framesByRule.Select(entry => new CodeMappingSelection.Candidate(entry.Key.Stack, entry.Key.Source, entry.Value)),
            unrooted,
            tied);
        return (rules, tied.Count > 0 && !unmatched);
    }

    private static readonly (string Stack, string Source) Identity = ("", "");

    // The repo files a frame matched equally well, and whether they share more with it than the file name.
    private sealed record Tie(IReadOnlyList<string> Files, bool BeyondFileName);

    // The rewrite (stackRoot, sourceRoot) for the repo path sharing the longest trailing-segment suffix
    // with the frame, or none when nothing matches. The caller has already skipped a frame that names a repo
    // file exactly, so the two roots always differ here. Two repo files sharing that longest suffix are a
    // tie between different source roots (the same suffix leaves the same stack root), and nothing in the
    // frame says which is its own: sibling apps from one template both hold src/App.tsx. A tie therefore
    // supports no rule and is returned as a tie, rather than picking one and routing every such frame into it.
    private static ((string Stack, string Source)? Rule, Tie? Tie) BestMatch(
        string stackPath, IReadOnlyList<(string Path, string[] Segments)> repos)
    {
        if (stackPath.Length == 0)
        {
            return (null, null);
        }
        var stackSegments = stackPath.Split('/');

        var bestDepth = 0;
        (string Stack, string Source)? best = null;
        List<string> atBest = [];
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
            // The deepest (most specific) match wins, and a deeper one settles any tie found so far.
            if (depth > bestDepth)
            {
                bestDepth = depth;
                best = (stackRoot, sourceRoot);
                atBest = [repoPath];
            }
            else if (depth == bestDepth)
            {
                atBest.Add(repoPath);
            }
        }
        return atBest.Count > 1 ? (null, new Tie(atBest, bestDepth > 1)) : (best, null);
    }

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
