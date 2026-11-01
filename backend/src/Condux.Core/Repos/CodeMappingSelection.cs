namespace Condux.Core.Repos;

/// <summary>
/// Chooses which of the rules <see cref="CodeMappingDeriver"/> derived to offer, so that the offered set,
/// saved as a whole, sends no sampled frame that matched a repository file to a different file, and a frame
/// that matched several equally well, by more than the file name, to a file outside them. That is all it
/// promises: it knows nothing of mappings already saved beside it, a frame that matched nothing can still
/// resolve somewhere, and it works on the deriver's normalized paths.
///
/// Two things make even that hard. A rule that rewrites from an empty stack root (a JVM frame is its
/// package path, so it maps as <c>"" → src/main/java/</c>) would rewrite every frame the project sends,
/// so it is first narrowed to the directory its frames share, per leading directory (<c>com/acme/ →
/// src/main/java/com/acme/</c>, with <c>org/</c> frames getting their own); a frame with no directory
/// gets a rule of its own file instead. And <see cref="CodeMapper"/> applies the longest matching stack
/// root, so any rule can take over another's frames: two modules narrowing to one root, one module's
/// root nesting above the other's frames, a Kotlin source set that leaves out its root package beside a
/// Java one.
///
/// So rules are added best supported first, and one goes in only if the frames of every rule taken so
/// far, its own included, still resolve to the files they matched. One that fails is split into a rule
/// per package directory, and a package that fails into a rule per file (a stack root that is the whole
/// frame path). A frame no rule can carry stays unmapped; a last pass takes out any rule that would send
/// one to the wrong file and, unless it was already one file's, brings its frames back one file each
/// where that misroutes nothing.
/// </summary>
internal static class CodeMappingSelection
{
    /// <summary>A derived rule and the frames that support it. A frame's file is the rule applied to it,
    /// <c>Source + frame[Stack.Length..]</c>, which is the repository file the deriver matched it to.</summary>
    public sealed record Candidate(string Stack, string Source, IReadOnlyList<string> Frames);

    /// <summary>The rules to offer. <paramref name="rules"/> are derived rules with their frames;
    /// <paramref name="unrooted"/> holds the frames of each empty-root rule that rewrites, keyed by its
    /// source root, which are narrowed here first; <paramref name="tied"/> holds each frame that supports no
    /// rule because it matched several files equally well by more than the file name, with those files.</summary>
    public static IReadOnlyList<DerivedMapping> Select(
        IEnumerable<Candidate> rules, IReadOnlyDictionary<string, List<string>> unrooted,
        IReadOnlyDictionary<string, IReadOnlyList<string>> tied)
    {
        // A frame with no directory has nothing to narrow to, so it gets a rule of its own file rather than
        // one from the empty root.
        var candidates = rules.Concat(unrooted.SelectMany(entry => entry.Value
                .GroupBy(ToLeadingDirectory, StringComparer.Ordinal)
                .SelectMany(group => group.Key.Length == 0
                    ? ToFiles(new Candidate("", entry.Key, [.. group]))
                    : [new Candidate(ToSharedDirectory([.. group]), entry.Key + ToSharedDirectory([.. group]), [.. group])])))
            .ToList();
        List<Sample> tiedSamples = [.. tied.Select(entry => new Sample(entry.Key, entry.Value))];
        var files = candidates.SelectMany(ToSamples).Concat(tiedSamples).ToList();

        var accepted = new List<Candidate>();
        foreach (var candidate in candidates
                     .OrderByDescending(candidate => candidate.Frames.Count)
                     .ThenBy(candidate => candidate.Source.Length)
                     .ThenBy(candidate => candidate.Source, StringComparer.Ordinal)
                     .ThenBy(candidate => candidate.Stack, StringComparer.Ordinal))
        {
            Accept(candidate, accepted, tiedSamples);
        }

        // A frame nothing could carry may still resolve through a rule taken for other frames. That rule goes,
        // and its own frames come back one file each where they can. A rule of one file is not split again,
        // so this ends.
        while (files.FirstOrDefault(sample => !Routes(sample, accepted)) is { } misrouted)
        {
            var longest = accepted.Where(rule => misrouted.Frame.StartsWith(rule.Stack, StringComparison.Ordinal))
                .Max(rule => rule.Stack.Length);
            var removed = accepted.Where(rule => rule.Stack.Length == longest
                && misrouted.Frame.StartsWith(rule.Stack, StringComparison.Ordinal)).ToList();
            accepted.RemoveAll(removed.Contains);
            foreach (var file in removed.Where(rule => !IsOneFile(rule)).SelectMany(ToFiles))
            {
                TryAdd(file, accepted, tiedSamples);
            }
        }

        return [.. accepted
            .GroupBy(rule => (rule.Stack, rule.Source))
            .Select(group => new DerivedMapping(group.Key.Stack, group.Key.Source, group.Sum(rule => rule.Frames.Count)))
            .OrderByDescending(rule => rule.MatchCount)
            .ThenBy(rule => rule.StackRoot, StringComparer.Ordinal)];
    }

    // Takes the rule, or failing that each of its packages, or failing that each of the package's files.
    // A rule of one file is as fine as a rule gets, so it is taken or left, never split: its stack root is
    // a whole path, not a directory, which is what the split by package relies on.
    private static void Accept(Candidate rule, List<Candidate> accepted, IReadOnlyList<Sample> tied)
    {
        if (TryAdd(rule, accepted, tied) || IsOneFile(rule))
        {
            return;
        }
        foreach (var package in ToPackages(rule))
        {
            if (!TryAdd(package, accepted, tied))
            {
                foreach (var file in ToFiles(package))
                {
                    TryAdd(file, accepted, tied);
                }
            }
        }
    }

    // Adds the rule if the frames of every rule taken so far, its own included, and the tied frames still
    // resolve to the files they matched. A rule can change only the frames its stack root prefixes, so only
    // those and its own are checked. While rules are first chosen every frame of a rule taken so far already
    // resolves correctly; a frame whose own rule was refused may not, and the final pass's check of every
    // frame is what catches that.
    private static bool TryAdd(Candidate rule, List<Candidate> accepted, IReadOnlyList<Sample> tied)
    {
        List<Candidate> trial = [.. accepted, rule];
        var affected = accepted.SelectMany(ToSamples).Concat(tied)
            .Where(sample => sample.Frame.StartsWith(rule.Stack, StringComparison.Ordinal))
            .Concat(ToSamples(rule));
        if (!affected.All(sample => Routes(sample, trial)))
        {
            return false;
        }
        accepted.Add(rule);
        return true;
    }

    // Whether a frame resolves through these rules, longest stack root first as CodeMapper applies them,
    // to a file it matched or to nothing (pinned by CodeMappingDeriverTests, which resolve each offered
    // set through CodeMapper itself). Rules tied on the longest root must agree, since CodeMapper then
    // takes whichever comes first and the stored order does not decide between them.
    private static bool Routes(Sample sample, IReadOnlyList<Candidate> rules)
    {
        var matching = rules.Where(rule => sample.Frame.StartsWith(rule.Stack, StringComparison.Ordinal)).ToList();
        if (matching.Count == 0)
        {
            return true;
        }
        var longest = matching.Max(rule => rule.Stack.Length);
        return matching.Where(rule => rule.Stack.Length == longest).All(rule => sample.Files.Contains(ToFile(rule, sample.Frame)));
    }

    // A sampled frame and the files it may resolve to: the one it matched, or each it tied between.
    private sealed record Sample(string Frame, IReadOnlyList<string> Files);

    private static IEnumerable<Sample> ToSamples(Candidate rule) =>
        rule.Frames.Select(frame => new Sample(frame, [ToFile(rule, frame)]));

    // Whether the rule is one file's: its stack root is the whole path of each of its frames.
    private static bool IsOneFile(Candidate rule) => rule.Frames.All(frame => frame == rule.Stack);

    // One rule per file: the whole frame path rewritten to the file it matched.
    private static IReadOnlyList<Candidate> ToFiles(Candidate candidate) =>
        [.. candidate.Frames.Distinct(StringComparer.Ordinal)
            .Select(frame => new Candidate(frame, ToFile(candidate, frame), [.. candidate.Frames.Where(other => other == frame)]))];

    // One rule per directory the candidate's frames sit in, each rewriting exactly as the candidate does.
    // A stack root ends at a directory boundary, so each frame's directory starts with it.
    private static IReadOnlyList<Candidate> ToPackages(Candidate candidate) =>
        [.. candidate.Frames
            .GroupBy(frame => ToSharedDirectory([frame]), StringComparer.Ordinal)
            .Select(package => new Candidate(
                package.Key, candidate.Source + package.Key[candidate.Stack.Length..], [.. package]))];

    private static string ToFile(Candidate rule, string frame) => rule.Source + frame[rule.Stack.Length..];

    // The first directory of a path, or "" for a path with none or a rooted one (/X.java), which is then
    // carried a file at a time like a frame with no directory: more rules, never wrong ones.
    private static string ToLeadingDirectory(string path) =>
        path.IndexOf('/') is var slash and > 0 ? path[..slash] : "";

    // The leading directories every path shares, with a trailing slash, or "" when they share none.
    private static string ToSharedDirectory(IReadOnlyList<string> paths)
    {
        var directories = paths.Select(path => path.Split('/')[..^1]).ToList();
        var depth = directories.Min(segments => segments.Length);
        var shared = 0;
        while (shared < depth && directories.All(segments => segments[shared] == directories[0][shared]))
        {
            shared++;
        }
        return shared == 0 ? "" : string.Join('/', directories[0][..shared]) + "/";
    }
}
