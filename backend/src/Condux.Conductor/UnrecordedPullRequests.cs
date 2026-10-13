using System.Text.Json;
using Condux.Core.FixEngine;
using Condux.Core.SourceControl;
using Condux.Storage.Postgres;
using Microsoft.Extensions.Logging;

namespace Condux.Conductor;

/// <summary>
/// Asks the source host, before the stale-run sweep fails a running run, whether that run had opened its
/// draft pull request. A run names its branch from its own id (<see cref="FixBranches"/>), so the branch
/// can be rebuilt from the row. A pull request found is recorded as the run's success, and the run is not
/// failed or refunded. A host that cannot be asked, or an org with no installation now, leaves the run for
/// a later pass; the sweep fails it anyway once it is past the pending cutoff.
///
/// A run written before branches were named this way is looked up under a name it never used, finds
/// nothing, and is failed as before.
/// </summary>
public sealed class UnrecordedPullRequests(
    ILogger<UnrecordedPullRequests> logger, PostgresStaleRunSweep sweep, IFixStore fixes,
    ProjectEventNotifier projectEvents, ISourceHostClient? repo = null, ISourceHostTokens? tokens = null)
{
    /// <summary>Checks every hosted run running since before <paramref name="cutoff"/> and returns those the
    /// sweep may fail: the ones the host says opened nothing. With no source host configured nothing can
    /// have opened a pull request, so every one is returned.</summary>
    public async Task<IReadOnlyCollection<Guid>> CheckAsync(
        DateTimeOffset cutoff, DateTimeOffset now, CancellationToken ct)
    {
        var cleared = new List<Guid>();
        foreach (var run in await sweep.ListRunningAsync(cutoff, ct))
        {
            if (repo is null || tokens is null)
            {
                cleared.Add(run.Id);
                continue;
            }
            if (run.InstallationId is not { } installationId)
            {
                continue;
            }

            var branch = FixBranches.ToBranch(run.Reference, run.Id);
            FoundPullRequest? found;
            try
            {
                var token = await tokens.GetAsync(installationId, ct);
                found = await repo.FindPullRequestAsync(token, run.RepoFullName, branch, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "could not look up the pull request of a stopped fix run id={RunId}", run.Id);
                continue;
            }

            if (found is null)
            {
                cleared.Add(run.Id);
            }
            else
            {
                await RecordAsync(run, branch, found, now);
            }
        }
        return cleared;
    }

    // The pull request exists, so from here a shutdown must not skip the record: no token.
    private async Task RecordAsync(UnrecordedRun run, string branch, FoundPullRequest found, DateTimeOffset now)
    {
        if (!await sweep.TryRecordFoundAsync(run, branch, found, now, CancellationToken.None))
        {
            return;
        }
        logger.LogWarning(
            "stopped fix run had opened its pull request id={RunId} cveBump={IsCveBump}", run.Id, run.IsCveBump);
        if (!run.IsCveBump)
        {
            // A bump has no audit trail (ADR-0023); its row is the whole record.
            await TryAuditAsync(run.Id, "draft_pr_found", new { prUrl = found.Url, branch });
            if (found.MergedAt is { } mergedAt)
            {
                await TryAuditAsync(run.Id, "fix_merged", new { prUrl = found.Url, mergedAt });
            }
        }
        await ProjectEventNudge.TrySendAsync(projectEvents, logger, run.ProjectId, CancellationToken.None);
    }

    private async Task TryAuditAsync(Guid runId, string eventName, object detail)
    {
        try
        {
            await fixes.AppendAuditAsync(
                runId, "conductor", eventName, JsonSerializer.Serialize(detail), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "found pull request not audited id={RunId}", runId);
        }
    }
}
