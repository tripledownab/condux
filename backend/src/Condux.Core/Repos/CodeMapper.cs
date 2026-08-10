namespace Condux.Core.Repos;

/// <summary>
/// Resolves a runtime stack-frame path (e.g. <c>/app/dist/checkout.js</c>) to a path inside the
/// linked repo (<c>src/checkout.js</c>) using the project's code mappings — <c>stack_root → source_root</c>
/// prefix rules. The longest matching <c>stack_root</c> wins (most specific), so general and specific
/// rules can coexist. Returns null when no mapping applies. This is the pure error→code linking step
/// the Conductor's context assembly builds on (#63); it needs no GitHub access.
/// </summary>
public static class CodeMapper
{
    public static string? Resolve(string framePath, IReadOnlyList<CodeMapping> mappings)
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

        return best is null ? null : best.SourceRoot + framePath[best.StackRoot.Length..];
    }
}
