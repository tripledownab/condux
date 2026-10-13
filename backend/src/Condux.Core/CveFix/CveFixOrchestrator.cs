using Condux.Core.FixEngine;

namespace Condux.Core.CveFix;

/// <summary>
/// Drives one CVE-bump run: record a PENDING run, hand the scoped bump context to the shared
/// <see cref="IFixProvider"/> (the same Conductor provider/gateway the issue path uses), and record the
/// resulting draft PR (or the failure). The CVE counterpart of <see cref="FixOrchestrator"/> for
/// the supply-chain
/// path; storage and the provider are injected, so this is pure orchestration. A failed run hands back the
/// monthly allowance the request reserved (a failed run opened no PR, so it stays free, matching the issue
/// path and ADR-0017; a Free lifetime slot is not refunded here, by design), through the store, which writes
/// the failure and the refund together.
/// </summary>
public sealed class CveFixOrchestrator(ICveFixStore store, IFixProvider provider)
{
    /// <summary>Run the bump for the job and return the final run (SUCCEEDED or FAILED), with the error
    /// that failed it, or null when the job is not run: it was delivered a second time, its advisory has
    /// another run in progress, or the run it inserted was ended before it could be marked running.</summary>
    public async Task<CveFixOutcome?> RunAsync(CveFixJob job, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var prompt = string.IsNullOrEmpty(job.Prompt)
            ? $"Bump {job.Package} to {job.ToVersion} to remediate {job.CveId ?? job.GhsaId}."
            : job.Prompt;
        var run = job.ToPendingRun(provider.Name, now);

        // The request wrote the run as pending before it published the job, so the job claims that run, as on
        // the issue path, and a job with none to claim inserts its own. A job delivered twice meets its own
        // row, and one whose advisory has another run in progress is not run either; the store hands its
        // reservation back. A run the stale-run sweep failed is not claimed, since its allowance went back
        // with it.
        var claimed = await store.TryClaimAsync(run, cancellationToken);
        if (claimed is null && !await store.InsertAsync(run, cancellationToken))
        {
            return null;
        }

        run = run with { Id = claimed ?? run.Id, Status = FixStatus.Running, UpdatedAt = DateTimeOffset.UtcNow };
        // The run this job inserted is pending until this write, so it can be ended in between, most likely by
        // an admin's cancel, which refunded it, or by its repository being unlinked. Either way it must not run.
        if (claimed is null && !await store.MarkRunningAsync(run, cancellationToken))
        {
            return null;
        }

        FixResult result;
        try
        {
            // The provider is issue-agnostic — the advisory id rides as the "issue id" (used only in the
            // branch name), the bump prompt + manifest paths are the scoped context.
            var request = new FixRequest(job.GhsaId, job.RepoFullName, job.BaseBranch, prompt, job.Model)
            {
                RunId = run.Id,
                ScopedPaths = job.ScopedPaths,
                InstallationId = job.InstallationId,
                OrgId = job.OrgId,
            };
            result = await provider.GenerateFixAsync(request, cancellationToken);
        }
        // A cancellation the caller did not ask for is a timeout inside the run (HttpClient reports its
        // own that way), so it fails the run like any other error. Only the caller's own cancellation,
        // which is the worker shutting down, passes through. The repair writes take no token: the run
        // has already failed, and a shutdown arriving now must not leave it Running with its allowance
        // held.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            run = run with
            {
                Status = FixStatus.Failed,
                FailureReason = FixFailures.Classify(ex),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            // The stale-run sweep may have concluded this run already, and refunded it, if it ran long. The
            // store refunds only when this write concludes it, in the same transaction as the failure.
            await store.TryFailAsync(run, CancellationToken.None);
            return new CveFixOutcome(run, ex);
        }

        // Outside the catch on purpose: the draft PR exists by now, so an error recording it must not mark
        // the run Failed and refund an allowance that was spent. No token either: a shutdown arriving now
        // would leave a real PR recorded nowhere, and the job is not delivered again.
        run = run with
        {
            Status = FixStatus.Succeeded,
            Branch = result.Branch,
            PrUrl = result.PrUrl,
            Summary = result.Summary,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await store.RecordSuccessAsync(run, CancellationToken.None);
        return new CveFixOutcome(run, Error: null);
    }
}

/// <summary>A bump's final run, and the error that failed it. The error is for the operator's log only:
/// a bump writes no audit, and its text is never stored on the run or served, since a provider's error
/// body can reach it.</summary>
public sealed record CveFixOutcome(CveFixRun Run, Exception? Error);
