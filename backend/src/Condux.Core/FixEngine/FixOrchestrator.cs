using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Condux.Core.FixEngine;

/// <summary>
/// Drives one fix run: create a PENDING suggestion, run the provider, record the resulting draft PR
/// (or the failure), and write an audit entry at each step. Storage and the provider are injected, so
/// this is pure orchestration, the safe spine the Conductor worker and the tests share. A failed run hands
/// back the reservation RequestFix took from the org's monthly allowance (failed runs delivered no PR, so
/// they stay free, ADR-0017), through the store, which writes the failure and the refund together.
/// </summary>
public sealed class FixOrchestrator(IFixStore store, IFixProvider provider)
{
    /// <summary>Run a fix for the job and return the final suggestion (SUCCEEDED or FAILED), or null when the
    /// job is not run: it was delivered a second time and its run already exists, or the run it inserted was
    /// ended before it could be marked running.</summary>
    public async Task<FixSuggestion?> RunAsync(FixJob job, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var prompt = string.IsNullOrEmpty(job.Prompt) ? $"Fix issue {job.IssueId}." : job.Prompt;
        var suggestion = job.ToPendingRun(provider.Name, now);

        // The request wrote the run as pending before it published the job, so the job claims that run. A job
        // with none to claim tries to insert its own, which only one published before requests wrote runs
        // can do: any other job meets its own run on the job id. Either way a job delivered twice
        // meets its own row and is not run again: running it would open a second draft PR on one reservation.
        // A run the stale-run sweep failed is not claimed either, since its allowance went back with it.
        var claimed = await store.TryClaimAsync(suggestion, cancellationToken);
        if (claimed is null && !await store.InsertAsync(suggestion, cancellationToken))
        {
            return null;
        }
        suggestion = suggestion with
        {
            Id = claimed ?? suggestion.Id,
            Status = FixStatus.Running,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        // The run this job inserted is pending until this write, so it can be ended in between, most likely by
        // an admin's cancel, which refunded it, or by its issue being deleted. Either way it must not run.
        if (claimed is null && !await store.MarkRunningAsync(suggestion, cancellationToken))
        {
            return null;
        }
        // Audit who triggered it (the actor column), the repo, the provider + model, and a hash of the exact
        // context prompt — enough to reconstruct the decision trail without persisting the (sensitive) prompt.
        // After the mark, so a run cancelled before it started never records a request after its cancel.
        await AuditAsync(
            suggestion.Id, job.Actor, "requested",
            new { repo = job.RepoFullName, model = job.Model, provider = provider.Name, promptHash = PromptHash(prompt) },
            cancellationToken);

        FixResult result;
        try
        {
            var request = new FixRequest(
                job.IssueId.ToString(), job.RepoFullName, job.BaseBranch, prompt, job.Model)
            {
                RunId = suggestion.Id,
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
            suggestion = suggestion with
            {
                Status = FixStatus.Failed,
                FailureReason = FixFailures.Classify(ex),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            // The stale-run sweep may have concluded this run already, and refunded it, if it ran long. The
            // store refunds only when this write concludes it, in the same transaction as the failure.
            var concludedHere = await store.TryFailAsync(suggestion, CancellationToken.None);
            // The error is the only record of what went wrong, so it is audited whether or not this write
            // concluded the run, under its own event when the sweep had. A run whose issue was deleted has no
            // row for the audit to reference, and that throw is left to the worker.
            await AuditAsync(
                suggestion.Id, job.Actor, concludedHere ? "failed" : "failed_late", new { error = ex.Message },
                CancellationToken.None);
            return suggestion;
        }

        // Outside the catch on purpose: the draft PR exists by now, so an error recording it must not mark
        // the run Failed and refund an allowance that was spent. No token either: a shutdown arriving now
        // would leave a real PR recorded nowhere, and the job is not delivered again.
        suggestion = suggestion with
        {
            Status = FixStatus.Succeeded,
            Branch = result.Branch,
            PrUrl = result.PrUrl,
            Summary = result.Summary,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await store.RecordSuccessAsync(suggestion, CancellationToken.None);
        // Token usage rides on the audit so per-org model cost is reconstructable for pricing (#100).
        await AuditAsync(
            suggestion.Id, job.Actor, "draft_pr_opened",
            new
            {
                prUrl = result.PrUrl,
                branch = result.Branch,
                summary = result.Summary,
                inputTokens = result.InputTokens,
                outputTokens = result.OutputTokens,
            }, CancellationToken.None);
        return suggestion;
    }

    private Task AuditAsync(Guid fixId, string actor, string eventName, object detail, CancellationToken ct) =>
        store.AppendAuditAsync(fixId, actor, eventName, JsonSerializer.Serialize(detail), ct);

    /// <summary>
    /// A stable fingerprint of the exact context sent to the provider, so the audit records which prompt
    /// was used without persisting the (potentially sensitive) prompt text.
    ///
    /// Public because the request path writes the same entry when it queues a run for a customer's runner
    /// rather than for this orchestrator. A second hash implementation there would be free to drift, and
    /// then the same prompt would fingerprint differently depending on where it ran.
    /// </summary>
    public static string PromptHash(string prompt) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));
}
