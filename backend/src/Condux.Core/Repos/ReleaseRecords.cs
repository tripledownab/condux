namespace Condux.Core.Repos;

/// <summary>
/// What a release may record, checked by every route that records one, and the commit shape frame links
/// require of a stored release, since rows recorded before the routes checked can hold anything. A release
/// names a deployed version and the commit it ran, which frame links point at and the Conductor reads its
/// suspect commit from, so a blank version or a commit that is not a commit id records nothing.
/// </summary>
public static class ReleaseRecords
{
    /// <summary>A refusal: the version is blank.</summary>
    public const string InvalidVersion = "invalid_version";

    /// <summary>A refusal: the commit is not a commit id.</summary>
    public const string InvalidCommit = "invalid_commit";

    /// <summary>The release to record, trimmed, or the refusal for a request that cannot be one.</summary>
    public static (RecordableRelease? Release, string? Error) ToRecordable(string? version, string? commit)
    {
        var trimmedVersion = version?.Trim();
        var trimmedCommit = commit?.Trim();
        if (string.IsNullOrEmpty(trimmedVersion))
        {
            return (null, InvalidVersion);
        }
        return IsCommitId(trimmedCommit)
            ? (new RecordableRelease(trimmedVersion, trimmedCommit!), null)
            : (null, InvalidCommit);
    }

    /// <summary>A SHA-1 or SHA-256 object id, full or abbreviated to at least the 7 characters git shortens
    /// to by default. Rows written before the routes checked are read through this too.</summary>
    public static bool IsCommitId(string? commit) =>
        commit is { Length: >= 7 and <= 64 } && commit.All(char.IsAsciiHexDigit);
}

/// <summary>A release request that passed <see cref="ReleaseRecords.ToRecordable"/>, the only thing that
/// builds one, so the repository that stores a release cannot be handed a value the rule never saw.</summary>
public sealed record RecordableRelease
{
    internal RecordableRelease(string version, string commit)
    {
        Version = version;
        Commit = commit;
    }

    public string Version { get; }

    public string Commit { get; }
}
