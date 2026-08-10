using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Condux.Core.Quotas;

namespace Condux.Core.FixEngine;

/// <summary>
/// Drives one fix run: create a PENDING suggestion, run the provider, record the resulting draft PR
/// (or the failure), and write an audit entry at each step. Storage and the provider are injected, so
/// this is pure orchestration — the safe spine the Conductor worker and the tests share. When a quota is
/// supplied, a failed run refunds the reservation RequestFix took from the org's monthly allowance
/// (failed runs delivered no PR, so they stay free — ADR-0017).
/// </summary>
public sealed class FixOrchestrator(IFixStore store, IFixProvider provider, IAiFixQuota? quota = null)
{
    /// <summary>Run a fix for the job and return the final suggestion (SUCCEEDED or FAILED).</summary>
    public async Task<FixSuggestion> RunAsync(FixJob job, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var prompt = string.IsNullOrEmpty(job.Prompt) ? $"Fix issue {job.IssueId}." : job.Prompt;
        var suggestion = new FixSuggestion(
            Guid.CreateVersion7(), job.IssueId, job.RepoFullName, FixStatus.Pending,
            provider.Name, job.Model, Branch: "", PrUrl: "", Summary: "", now, now);

        await store.InsertAsync(suggestion, cancellationToken);
        // Audit who triggered it (the actor column), the repo, the provider + model, and a hash of the exact
        // context prompt — enough to reconstruct the decision trail without persisting the (sensitive) prompt.
        await AuditAsync(
            suggestion.Id, job.Actor, "requested",
            new { repo = job.RepoFullName, model = job.Model, provider = provider.Name, promptHash = PromptHash(prompt) },
            cancellationToken);

        suggestion = suggestion with { Status = FixStatus.Running, UpdatedAt = DateTimeOffset.UtcNow };
        await store.UpdateAsync(suggestion, cancellationToken);

        try
        {
            var request = new FixRequest(
                job.IssueId.ToString(), job.RepoFullName, job.BaseBranch, prompt, job.Model)
            {
                ScopedPaths = job.ScopedPaths,
                InstallationId = job.InstallationId,
                OrgId = job.OrgId,
            };
            var result = await provider.GenerateFixAsync(request, cancellationToken);

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
            await store.UpdateAsync(suggestion, cancellationToken);
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
                }, cancellationToken);
            return suggestion;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            suggestion = suggestion with { Status = FixStatus.Failed, UpdatedAt = DateTimeOffset.UtcNow };
            await store.UpdateAsync(suggestion, cancellationToken);
            await AuditAsync(suggestion.Id, job.Actor, "failed", new { error = ex.Message }, cancellationToken);
            if (quota is not null && job.OrgId != 0)
            {
                await quota.RefundAsync(job.OrgId, DateTimeOffset.UtcNow, cancellationToken);
            }
            return suggestion;
        }
    }

    private Task AuditAsync(Guid fixId, string actor, string eventName, object detail, CancellationToken ct) =>
        store.AppendAuditAsync(fixId, actor, eventName, JsonSerializer.Serialize(detail), ct);

    // A stable fingerprint of the exact context sent to the provider, so the audit records which prompt was
    // used without persisting the (potentially sensitive) prompt text.
    private static string PromptHash(string prompt) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));
}
