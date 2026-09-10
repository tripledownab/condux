using Condux.Core.FixEngine;
using Condux.Core.Quotas;

namespace Condux.Core.CveFix;

/// <summary>
/// Drives one CVE-bump run: record a PENDING run, hand the scoped bump context to the shared
/// <see cref="IFixProvider"/> (the same Conductor provider/gateway the issue path uses), and record the
/// resulting draft PR (or the failure). The CVE counterpart of <see cref="FixOrchestrator"/> for
/// the supply-chain
/// path — storage and the provider are injected, so this is pure orchestration. When a quota is supplied,
/// a failed run refunds the monthly allowance the request reserved (a failed run opened no PR, so it stays
/// free — matching the issue path and ADR-0017; a Free lifetime slot is not refunded here, by design).
/// </summary>
public sealed class CveFixOrchestrator(ICveFixStore store, IFixProvider provider, IAiFixQuota? quota = null)
{
    /// <summary>Run the bump for the job and return the final run (SUCCEEDED or FAILED).</summary>
    public async Task<CveFixRun> RunAsync(CveFixJob job, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var prompt = string.IsNullOrEmpty(job.Prompt)
            ? $"Bump {job.Package} to {job.ToVersion} to remediate {job.CveId ?? job.GhsaId}."
            : job.Prompt;
        var run = new CveFixRun(
            Guid.CreateVersion7(), job.RepoLinkId, job.GhsaId, job.CveId, job.Package, job.Ecosystem,
            job.FromRange, job.ToVersion, job.AdvisoryUrl, FixStatus.Pending, provider.Name, job.Model,
            Branch: "", PrUrl: "", Summary: "", job.Actor, now, now);

        await store.InsertAsync(run, cancellationToken);

        run = run with { Status = FixStatus.Running, UpdatedAt = DateTimeOffset.UtcNow };
        await store.UpdateAsync(run, cancellationToken);

        try
        {
            // The provider is issue-agnostic — the advisory id rides as the "issue id" (used only in the
            // branch name), the bump prompt + manifest paths are the scoped context.
            var request = new FixRequest(job.GhsaId, job.RepoFullName, job.BaseBranch, prompt, job.Model)
            {
                ScopedPaths = job.ScopedPaths,
                InstallationId = job.InstallationId,
                OrgId = job.OrgId,
            };
            var result = await provider.GenerateFixAsync(request, cancellationToken);

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
            await store.UpdateAsync(run, cancellationToken);
            return run;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run = run with { Status = FixStatus.Failed, UpdatedAt = DateTimeOffset.UtcNow };
            await store.UpdateAsync(run, cancellationToken);
            if (quota is not null && job.OrgId != 0)
            {
                await quota.RefundAsync(job.OrgId, DateTimeOffset.UtcNow, cancellationToken);
            }
            return run;
        }
    }
}
