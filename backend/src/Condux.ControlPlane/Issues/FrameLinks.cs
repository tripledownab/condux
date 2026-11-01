using Condux.Core.Events;
using Condux.Core.Repos;
using Condux.GitHub;
using Condux.Storage.ClickHouse;

namespace Condux.ControlPlane.Issues;

/// <summary>
/// The GitHub link for each frame of an event's crashing exception, built here from the project's code
/// mappings (<see cref="CodeMapper"/>) so the browser holds no copy of the mapping rule. Mappings from
/// every linked repository take part, and the mapping that applies names the repository the link
/// points into, unless another repository maps the same stack root. Only an in-app frame is linked;
/// library code is not in the repository. A link names the commit the event's release was recorded at
/// when that release is recorded against the same repository, so the line anchor stays on that release's
/// code; otherwise, or when the stored commit is not a commit id, it names the repository's default branch.
/// </summary>
internal static class FrameLinks
{
    /// <summary>One list per event: each frame of its primary exception that has a link, by its position in
    /// stored order (oldest first). An event that cannot be read gets an empty list. <paramref name="releases"/>
    /// holds the recorded releases the events name, by version.</summary>
    public static IReadOnlyList<IReadOnlyList<FrameLink>> ToLinks(
        IReadOnlyList<StoredEvent> events, IReadOnlyList<RepoLink> repos, IReadOnlyList<CodeMapping> mappings,
        IReadOnlyList<Release> releases) =>
        [.. events.Select(stored => ToLinks(stored, repos, mappings, releases))];

    /// <summary>The release versions the events name, which the caller looks up to pass to ToLinks.</summary>
    public static IReadOnlyList<string> ToReleaseVersions(IReadOnlyList<StoredEvent> events) =>
        [.. events.Select(stored => stored.ToEvent()?.Release)
            .Where(version => !string.IsNullOrWhiteSpace(version)).OfType<string>().Distinct()];

    private static IReadOnlyList<FrameLink> ToLinks(
        StoredEvent stored, IReadOnlyList<RepoLink> repos, IReadOnlyList<CodeMapping> mappings,
        IReadOnlyList<Release> releases)
    {
        var sample = stored.ToEvent();
        var frames = sample is null ? null : EventExceptions.ResolvePrimary(sample)?.Stacktrace?.Frames;
        var release = string.IsNullOrWhiteSpace(sample?.Release)
            ? null
            : releases.FirstOrDefault(recorded => recorded.Version == sample.Release);
        return frames is null
            ? []
            : [.. frames
                .Select((frame, position) =>
                    ToLink(frame, repos, mappings, release) is { } url ? new FrameLink(position, url) : null)
                .OfType<FrameLink>()];
    }

    private static string? ToLink(
        Frame? frame, IReadOnlyList<RepoLink> repos, IReadOnlyList<CodeMapping> mappings, Release? release)
    {
        if (frame is not { InApp: true, Filename: { } filename }
            || CodeMapper.FindMapping(filename, mappings) is not { } mapping
            || IsClaimedByAnotherRepo(mapping, mappings)
            || repos.FirstOrDefault(repo => repo.Id == mapping.RepoLinkId) is not { } repo)
        {
            return null;
        }

        var path = CodeMapper.Apply(mapping, filename);
        // Rows recorded before both routes checked the commit can hold anything, so anything but a commit
        // id (a stray space, a blank) falls back rather than link to a 404.
        var atCommit = release is { } ran && ran.RepoLinkId == repo.Id && ReleaseRecords.IsCommitId(ran.CommitSha)
            ? GitHubLinks.ToBlobUrl(repo.RepoFullName, ran.CommitSha, path, frame.Lineno)
            : null;
        return atCommit ?? GitHubLinks.ToBlobUrl(repo.RepoFullName, repo.DefaultBranch, path, frame.Lineno);
    }

    // Two repositories mapping the same stack root tie, and CodeMapper would pick by list order, so the
    // frame could link into either. It links into neither rather than guess.
    private static bool IsClaimedByAnotherRepo(CodeMapping mapping, IReadOnlyList<CodeMapping> mappings) =>
        mappings.Any(other => other.RepoLinkId != mapping.RepoLinkId && other.StackRoot == mapping.StackRoot);
}
