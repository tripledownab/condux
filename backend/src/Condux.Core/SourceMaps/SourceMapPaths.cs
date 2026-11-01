namespace Condux.Core.SourceMaps;

/// <summary>
/// Turns a source map's <c>sources</c> entry into the path it names inside the repository, and says when
/// that path is someone else's code. Bundlers write sources with their own prefixes: Turbopack as
/// <c>turbopack:///[project]/web/src/x.tsx</c>, where <c>[project]</c> is the workspace root, and webpack as
/// <c>webpack://&lt;namespace&gt;/./src/x.ts</c>, relative to the app. Left as they are, a symbolicated frame
/// names a file no code mapping and no repository lookup can match, so the Conductor reads nothing.
/// </summary>
public static class SourceMapPaths
{
    private static readonly string[] Schemes = ["turbopack:///", "webpack:///", "webpack://"];

    /// <summary>The source with bundler prefixes removed, else unchanged. Never guesses beyond the forms
    /// named here, since an unrecognised shape is better left for a code mapping than rewritten wrongly.</summary>
    public static string ToRepoPath(string source)
    {
        var path = source;
        foreach (var scheme in Schemes)
        {
            if (!path.StartsWith(scheme, StringComparison.Ordinal))
            {
                continue;
            }

            path = path[scheme.Length..];
            // webpack://<namespace>/: the namespace names the bundle, not a directory. It defaults to the
            // package name, so a scoped package spends two segments on it (@acme/web). Turbopack's and
            // webpack:///'s have none, and their first segment is a real one ([project] or the path).
            if (scheme == "webpack://")
            {
                path = SkipSegments(path, path.StartsWith('@') ? 2 : 1);
            }

            break;
        }

        // webpack appends ?[hash] when two modules would share a name, and a query never names a file.
        if (path.IndexOf('?') is var query and >= 0)
        {
            path = path[..query];
        }

        if (path.StartsWith("[project]/", StringComparison.Ordinal))
        {
            path = path["[project]/".Length..];
        }

        while (path.StartsWith("./", StringComparison.Ordinal))
        {
            path = path[2..];
        }

        // A source that was only a prefix and a query (webpack://_N_E/?1234) names no file at all.
        return path.Length > 0 ? path : source;
    }

    private static string SkipSegments(string path, int count)
    {
        var start = 0;
        for (var skipped = 0; skipped < count; skipped++)
        {
            var slash = path.IndexOf('/', start);
            if (slash < 0)
            {
                return path;
            }

            start = slash + 1;
        }

        return path[start..];
    }

    /// <summary>Installed or bundler-runtime code, which the Conductor must not treat as the app's own:
    /// a minified chunk is in-app as a whole, but the lines inside it come from many packages. A map's own
    /// <c>ignoreList</c> says the same where it is present (<see cref="OriginalPosition.Ignored"/>), and
    /// the two are combined rather than one replacing the other: a bundler's list can mark wrappers no
    /// path gives away and still leave installed packages and its own runtime out (Next's webpack list
    /// misses a scoped namespace's runtime and some externals), and the webpack 5 Next bundles writes none
    /// by itself.
    /// </summary>
    public static bool IsDependency(string repoPath) =>
        repoPath.StartsWith("node_modules/", StringComparison.Ordinal)
        || repoPath.Contains("/node_modules/", StringComparison.Ordinal)
        || repoPath.StartsWith("[turbopack]/", StringComparison.Ordinal)
        // webpack 4 and later name the bootstrap webpack/bootstrap, and webpack 3 appended " <hash>" to it.
        || repoPath == "webpack/bootstrap"
        || repoPath.StartsWith("webpack/bootstrap ", StringComparison.Ordinal)
        || repoPath.StartsWith("webpack/runtime/", StringComparison.Ordinal)
        // webpack 5's startup modules, written when the runtime is its own chunk or an entry dependsOn another.
        || repoPath is "webpack/startup" or "webpack/before-startup" or "webpack/after-startup"
        // webpack 4 names a file inside its own package (webpack)/..., its buildin modules among them.
        || repoPath.StartsWith("(webpack)/", StringComparison.Ordinal)
        // webpack 5 names a module the bundle does not contain external <type> "<request>", and one aliased
        // to false ignored|<context>|<request>.
        || repoPath.StartsWith("external ", StringComparison.Ordinal)
        || repoPath.StartsWith("ignored|", StringComparison.Ordinal);
}
