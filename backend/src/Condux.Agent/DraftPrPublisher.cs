using Condux.Core.FixEngine;
using Condux.Core.SourceControl;

namespace Condux.Agent;

/// <summary>
/// The host-side git tail every gateway shares (ADR-0038): new branch off the base, one commit per
/// changed file, then the DRAFT pull request. This is where the safety invariant lives — the host does
/// all git with the full-permission token, so no execution environment (local sandbox or vendor-hosted)
/// ever needs a write-capable credential, and nothing but a draft PR can come out of a run.
/// </summary>
internal static class DraftPrPublisher
{
    public static async Task<(string Branch, string PrUrl, string Summary)> PublishAsync(
        ISourceHostClient repo, string token, AgentRunSpec spec, string runId,
        IReadOnlyDictionary<string, string> files, string proposalSummary,
        IReadOnlyList<RepoFile>? prefetched = null, CancellationToken ct = default)
    {
        var branch = $"condux/fix-{spec.IssueId}-{runId[..Math.Min(8, runId.Length)]}";
        var baseSha = await repo.GetBranchHeadShaAsync(token, spec.RepoFullName, spec.BaseBranch, ct);
        await repo.CreateBranchAsync(token, spec.RepoFullName, branch, baseSha, ct);
        foreach (var (path, contents) in files)
        {
            var existing = prefetched?.FirstOrDefault(f => f.Path == path)
                ?? await repo.GetFileAsync(token, spec.RepoFullName, path, spec.BaseBranch, ct);
            await repo.PutFileAsync(
                token, spec.RepoFullName, path, branch,
                $"Condux fix for issue {spec.IssueId}: {path}", contents, existing?.Sha, ct);
        }

        var summary = proposalSummary.Length > 0 ? proposalSummary : $"Draft fix for issue {spec.IssueId}.";
        var prUrl = await repo.OpenDraftPullRequestAsync(
            token, spec.RepoFullName, branch, spec.BaseBranch,
            title: Truncate($"Condux fix: {summary}", 90),
            body: $"{summary}\n\n---\nOpened as a draft by the Condux Conductor for issue {spec.IssueId}. "
                + "Review carefully before merging; Condux never merges automatically.", ct);
        return (branch, prUrl, summary);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
