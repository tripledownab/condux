using Condux.Core.FixEngine;
using Condux.Core.SourceControl;

namespace Condux.Agent;

/// <summary>
/// The host-side git tail every gateway shares (ADR-0038): new branch off the base, one commit per
/// changed file, then the DRAFT pull request. This is where the safety invariant lives — the host does
/// all git with the full-permission token, so no execution environment (local sandbox or vendor-hosted)
/// ever needs a write-capable credential, and nothing but a draft PR can come out of a run.
///
/// It is also where every model-chosen path is checked, for the same reason: both gateways call this and
/// it is the only caller of <c>PutFileAsync</c>, so a check here cannot be skipped by adding a third
/// gateway. The managed-agents gateway used to carry its own copy of the rule and the single-shot one
/// carried none, which is exactly what a per-caller check produces.
/// </summary>
internal static class DraftPrPublisher
{
    public static async Task<(string Branch, string PrUrl, string Summary)> PublishAsync(
        ISourceHostClient repo, string token, AgentRunSpec spec,
        IReadOnlyDictionary<string, string> files, string proposalSummary, FilesSeen seen,
        IReadOnlyCollection<string> leftUnread,
        IReadOnlyList<RepoFile>? prefetched = null, CancellationToken ct = default)
    {
        // Before the first git call: a run that names a path outside the repository fails without having
        // created a branch, rather than part way through committing.
        foreach (var path in files.Keys)
        {
            RepoPaths.Normalize(path);
        }

        // From the fix run's id, which its row keeps, so the stale-run sweep can find this pull request
        // for a run that stopped before it recorded one.
        var branch = FixBranches.ToBranch(spec.IssueId, spec.RunId);
        var summary = proposalSummary.Length > 0 ? proposalSummary : $"Draft fix for issue {spec.IssueId}.";

        // A runner whose lease lapsed hands the same run to the next runner, which names the same branch.
        // If the first attempt got as far as the pull request, that is this run's answer; opening another
        // would fail on the existing branch, and a run recorded as failed would leave its pull request
        // recorded nowhere. Only the retry gains from asking, so a lookup that fails costs the run nothing:
        // it goes on as if nothing were found, and an existing branch still refuses the push.
        if (await TryFindOpenedAsync(repo, token, spec.RepoFullName, branch, ct) is { } opened)
        {
            return (branch, opened.Url, summary);
        }

        // Every file's current state, before the branch exists. A change carries a file's complete contents,
        // so one to an existing file the run never saw replaces that file with a guess; that fails the run
        // here rather than becoming a commit.
        var existing = new Dictionary<string, RepoFile?>(StringComparer.Ordinal);
        foreach (var path in files.Keys)
        {
            existing[path] = prefetched?.FirstOrDefault(f => f.Path == path)
                ?? await ToCurrentAsync(repo, token, spec, path, ct);
            if (existing[path] is { } current && !seen.Includes(path, current.Sha))
            {
                throw new FixFailedException(
                    FixFailureReason.NoUsableFix,
                    $"The proposed fix replaces {path}, which exists in the repository, without the run having "
                    + "read it whole as it is now, so it was not committed.");
            }
            // The run saw this file and it is gone now: deleted on the branch since. Writing it would put
            // it back, undoing that deletion.
            if (existing[path] is null && seen.HasSeen(path))
            {
                throw new FixFailedException(
                    FixFailureReason.NoUsableFix,
                    $"The proposed fix writes {path}, which was deleted from the branch after the run read it, "
                    + "so it was not committed.");
            }
        }

        var baseSha = await repo.GetBranchHeadShaAsync(token, spec.RepoFullName, spec.BaseBranch, ct);
        await repo.CreateBranchAsync(token, spec.RepoFullName, branch, baseSha, ct);
        foreach (var (path, contents) in files)
        {
            await repo.PutFileAsync(
                token, spec.RepoFullName, path, branch,
                $"Condux fix for issue {spec.IssueId}: {path}", contents, existing[path]?.Sha, ct);
        }

        var prUrl = await repo.OpenDraftPullRequestAsync(
            token, spec.RepoFullName, branch, spec.BaseBranch,
            title: Truncate($"Condux fix: {summary}", 90),
            body: $"{summary}\n\n{ToUnreadNote(leftUnread)}---\nOpened as a draft by the Condux Conductor for issue {spec.IssueId}. "
                + "Review carefully before merging; Condux never merges automatically.", ct);
        return (branch, prUrl, summary);
    }

    // A path the source host answers as something other than a file (a directory, a link) is one the plan
    // cannot write as a file either, so it fails as an unusable fix rather than as an unclassified error.
    private static async Task<RepoFile?> ToCurrentAsync(
        ISourceHostClient repo, string token, AgentRunSpec spec, string path, CancellationToken ct)
    {
        try
        {
            return await repo.GetFileAsync(token, spec.RepoFullName, path, spec.BaseBranch, ct);
        }
        catch (InvalidOperationException error) when (error is not FixFailedException)
        {
            throw new FixFailedException(
                FixFailureReason.NoUsableFix, $"The proposed fix writes {path}, which is not a file it can replace.", error);
        }
    }

    // Written by Condux rather than left to the model's summary, so a reviewer is told every time. Two
    // spellings of one path are one file, and a path is written so a backtick in it cannot end the span.
    private static string ToUnreadNote(IReadOnlyCollection<string> leftUnread)
    {
        var paths = leftUnread.Select(RepoPaths.Normalize).Distinct(StringComparer.Ordinal).ToList();
        return paths.Count == 0
            ? ""
            : "**Left unchanged:** "
                + string.Join(", ", paths.Select(ToCodeSpan))
                + ". The Conductor could not read these in full, so it did not change them. Check whether the "
                + "fix also needed changes there; a lockfile after a version bump needs regenerating before "
                + "merging.\n\n";
    }

    // A code span delimited by one more backtick than the longest run inside it, per CommonMark.
    private static string ToCodeSpan(string text)
    {
        var longest = 0;
        var run = 0;
        foreach (var c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        var fence = new string('`', longest + 1);
        return longest == 0 ? $"`{text}`" : $"{fence} {text} {fence}";
    }

    private static async Task<FoundPullRequest?> TryFindOpenedAsync(
        ISourceHostClient repo, string token, string repoFullName, string branch, CancellationToken ct)
    {
        try
        {
            return await repo.FindPullRequestAsync(token, repoFullName, branch, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
