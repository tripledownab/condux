namespace Condux.Core.Repos;

/// <summary>
/// Resolves a runtime stack-frame path (e.g. <c>/app/dist/checkout.js</c>) to a path inside the
/// linked repo (<c>src/checkout.js</c>) using the project's code mappings — <c>stack_root → source_root</c>
/// prefix rules. The longest matching <c>stack_root</c> wins (most specific), so general and specific
/// rules can coexist. Returns null when no mapping applies. This is the pure error→code linking step
/// the Conductor's context assembly builds on (#63), and the issue page's GitHub links are built from it
/// on the server, so the rule has no second copy in the browser. It needs no GitHub access.
/// </summary>
public static class CodeMapper
{
    public static string? Resolve(string framePath, IReadOnlyList<CodeMapping> mappings) =>
        FindMapping(framePath, mappings) is { } best ? Apply(best, framePath) : null;

    /// <summary>The path a mapping rewrites this frame path to. The caller has found the mapping with
    /// <see cref="FindMapping"/>, so its stack root prefixes the path.</summary>
    public static string Apply(CodeMapping mapping, string framePath) =>
        mapping.SourceRoot + framePath[mapping.StackRoot.Length..];

    /// <summary>The mapping <see cref="Resolve"/> applies to this path, which also names the repository
    /// the path belongs to; null when none applies.</summary>
    public static CodeMapping? FindMapping(string framePath, IReadOnlyList<CodeMapping> mappings)
    {
        CodeMapping? best = null;
        foreach (var mapping in mappings)
        {
            if (framePath.StartsWith(mapping.StackRoot, StringComparison.Ordinal)
                && (best is null || mapping.StackRoot.Length > best.StackRoot.Length))
            {
                best = mapping;
            }
        }

        return best;
    }
}
