using Condux.Core.SourceControl;

namespace Condux.GitHub;

/// <summary>
/// Links into a repository on github.com. Each part of the URL is escaped segment by segment through
/// <see cref="RepoPaths"/>, because the file path comes from an event's stack frame, which whoever holds
/// the DSN chooses, and the repository name and branch are stored with no shape check: unescaped, an
/// encoded dot segment (<c>%2e%2e</c>) reads as <c>..</c> to a browser and leaves the repository for any
/// page on github.com.
/// </summary>
public static class GitHubLinks
{
    /// <summary>The blob URL for a file at a line (no anchor when the line is unknown), or null when the
    /// path is not inside the repository or the name or branch would climb out of its URL.</summary>
    public static string? ToBlobUrl(string repoFullName, string branch, string path, int line)
    {
        // Each RepoPaths call refuses what it cannot place by throwing, which is the null answer here.
        try
        {
            var anchor = line > 0 ? $"#L{line}" : "";
            return $"https://github.com/{RepoPaths.EscapeSegments(repoFullName)}/blob/"
                + $"{RepoPaths.EscapeSegments(branch)}/{RepoPaths.ToUrlPath(path)}{anchor}";
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
