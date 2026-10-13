namespace Condux.Core.Ingest;

/// <summary>
/// Gives a JVM frame the source path its class lives at. A JVM stack trace names only the file
/// (<c>Owner.java</c>) and puts the package in the class name (<c>org.example.owner.Owner</c>), which is how
/// our JVM SDK and stock Sentry Java SDKs both send it. A bare file name has no prefix for a code mapping to
/// match, so nothing could turn it into a repository path. The package can: by convention it is the directory
/// the source file sits under, below a source root such as <c>src/main/java/</c> that a mapping then supplies.
/// </summary>
public static class JvmFramePaths
{
    // The JVM languages whose stack traces carry a source file name. A file with any other extension keeps its
    // name, which is what keeps another runtime's dotted module (Python's app.views) from reading as a package.
    private static readonly string[] SourceExtensions = [".java", ".kt", ".kts", ".scala", ".groovy"];

    /// <summary>
    /// The package path plus the file name, for a JVM frame that sends a bare file name and its class name;
    /// otherwise the file name unchanged. A class in the default package has no directory, and a nested
    /// class (<c>Owner$Pet</c>) shares its outer class's file, so only the segments before the class count.
    /// </summary>
    public static string? ToSourcePath(string? filename, string? module)
    {
        if (string.IsNullOrEmpty(filename) || string.IsNullOrEmpty(module)
            || filename.Contains('/') || filename.Contains('\\')
            || !SourceExtensions.Any(ext => filename.EndsWith(ext, StringComparison.Ordinal)))
        {
            return filename;
        }

        // Only a class name: dotted segments, none empty, no separator (a module-qualified form such as
        // java.base/java.util.ArrayList names a module, not a directory).
        var segments = module.Split('.');
        if (segments.Length < 2 || segments.Any(s => s.Length == 0 || s.Contains('/') || s.Contains('\\')))
        {
            return filename;
        }
        return $"{string.Join('/', segments[..^1])}/{filename}";
    }
}
