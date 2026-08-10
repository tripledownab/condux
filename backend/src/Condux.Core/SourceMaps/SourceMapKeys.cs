namespace Condux.Core.SourceMaps;

/// <summary>
/// Derives the object-storage key for a source map (ADR-0028). Pure and deterministic, so re-uploading the
/// same artifact overwrites the same key (idempotent CI). Keyed by debugId when present (the stable build
/// identity), else by the (release, dist, filename) tuple.
/// </summary>
public static class SourceMapKeys
{
    public static string ForArtifact(long projectId, string release, string? dist, string? debugId, string filename)
    {
        if (!string.IsNullOrWhiteSpace(debugId))
        {
            return $"sourcemaps/{projectId}/by-debug-id/{Sanitize(debugId)}";
        }

        var scopedDist = string.IsNullOrWhiteSpace(dist) ? "_" : Sanitize(dist);
        return $"sourcemaps/{projectId}/by-release/{Sanitize(release)}/{scopedDist}/{Sanitize(filename)}";
    }

    // Object-store keys must be predictable path segments: keep alphanumerics + . - _, collapse the rest
    // to '_'. This also blocks a caller-supplied filename from injecting path traversal into the key.
    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray();
        return chars.Length == 0 ? "_" : new string(chars);
    }
}
