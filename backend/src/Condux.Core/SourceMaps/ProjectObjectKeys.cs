using System.Globalization;

namespace Condux.Core.SourceMaps;

/// <summary>
/// The layout every uploaded artifact shares in object storage: a root per kind, then the project's id,
/// then whatever the kind keys on. Keeping the project id second is what lets a deleted project's objects
/// be found at all, since deleting a project removes the rows that named them: the project delete trigger
/// queues one prefix per root (migration 0062).
/// </summary>
public static class ProjectObjectKeys
{
    public const string SourceMaps = "sourcemaps/";

    public const string DebugFiles = "debug-files/";

    /// <summary>Every root an upload writes under.</summary>
    public static readonly IReadOnlyList<string> Roots = [SourceMaps, DebugFiles];

    /// <summary>The prefix holding one project's objects of one kind, ending in a separator so that
    /// project 1 never matches project 12.</summary>
    public static string ToPrefix(string root, long projectId) =>
        $"{root}{projectId.ToString(CultureInfo.InvariantCulture)}/";

    /// <summary>The project a key under <paramref name="root"/> belongs to, or null when the key does not
    /// follow the layout: the segment after the root is not a project id as <see cref="ToPrefix"/> writes
    /// one, or nothing follows it.</summary>
    public static long? ToProjectId(string root, string key)
    {
        if (!key.StartsWith(root, StringComparison.Ordinal))
        {
            return null;
        }
        var rest = key.AsSpan(root.Length);
        var slash = rest.IndexOf('/');
        // A project id is never written with a leading zero, so "007" is not project 7's.
        return slash > 0 && slash < rest.Length - 1 && rest[0] != '0'
            && long.TryParse(rest[..slash], NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
    }
}
