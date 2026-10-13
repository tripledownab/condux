using Condux.Core.CveFix;
using Condux.Core.FixEngine;
using Microsoft.Extensions.Logging;

namespace Condux.Storage.Postgres;

/// <summary>
/// Sends a reserved fix, for an issue or a CVE bump, where the org has its fixes run (ADR-0033 slice 4c):
/// a leasable row its own runner takes, or a pending run plus a job on the topic our Conductor drains.
/// Everything before this, the context, the double scrub, the cost cap and the allowance, is identical
/// either way, which is the point. Either way the run is written before anything can execute it, so a
/// second request for the same work meets it and is refused. Shared by the request endpoints and the
/// auto-fix dispatcher, which routed separately before and had begun to differ.
/// </summary>
public static class FixEnqueue
{
    public static async Task<FixQueueResult> EnqueueAsync(
        FixExecution execution, FixJob job, FixContext context, PostgresJobLeaseStore leases, IFixStore fixes,
        IFixRequestPublisher publisher, ILogger logger, CancellationToken cancellationToken)
    {
        if (execution != FixExecution.Runner)
        {
            var run = job.ToPendingRun(provider: "", DateTimeOffset.UtcNow);
            return await FixRunQueue.QueueAsync(
                ct => fixes.TryQueueAsync(run, ct), ct => publisher.PublishAsync(job, ct),
                ct => fixes.TryCancelQueuedAsync(run, ct), cancellationToken);
        }

        // No token on the insert: a hang-up must not orphan the run. A failure is settled like a hosted one,
        // by cancelling the row, since an insert can commit and still report an error, and refunding a row a
        // runner then leases would let it run for nothing.
        var row = job.ToPendingRun(PostgresJobLeaseStore.RunnerProvider, DateTimeOffset.UtcNow);
        var queued = await FixRunQueue.QueueForRunnerAsync(
            ct => leases.EnqueueAsync(
                row.Id, job.IssueId, job.RepoFullName,
                new RunnerJobContext(job.BaseBranch, context.Prompt, context.ScopedPaths), job.ReservedAt, ct),
            ct => fixes.TryCancelQueuedAsync(row, ct));
        if (queued.Outcome != FixQueueOutcome.Queued)
        {
            return queued;
        }

        // The hosted orchestrator writes this when it starts the run; a run we hand to a runner never
        // reaches it, so without this the trail begins at "leased" and nothing records who asked for the
        // fix. fix_suggestions has no actor column, so that fact would exist nowhere. Best-effort, because
        // the row is committed: failing here would refund a run that is going to happen anyway.
        await FixAudit.TryWriteAsync(
            fixes, logger, row.Id, job.Actor, "requested",
            new
            {
                repo = job.RepoFullName,
                model = "",
                provider = PostgresJobLeaseStore.RunnerProvider,
                promptHash = FixOrchestrator.PromptHash(context.Prompt),
            },
            cancellationToken);
        // Queued, with the failure kept when the insert reported one and a runner had leased the row anyway.
        return queued;
    }

    public static async Task<FixQueueResult> EnqueueCveAsync(
        FixExecution execution, CveFixJob job, FixContext context, PostgresJobLeaseStore leases,
        ICveFixStore cveFixes, ICveFixPublisher publisher, CancellationToken cancellationToken)
    {
        if (execution != FixExecution.Runner)
        {
            var run = job.ToPendingRun(provider: "", DateTimeOffset.UtcNow);
            return await FixRunQueue.QueueAsync(
                ct => cveFixes.TryQueueAsync(run, ct), ct => publisher.PublishAsync(job, ct),
                ct => cveFixes.TryCancelQueuedAsync(run, ct), cancellationToken);
        }

        // As for an issue fix. A bump writes no audit: its run row is the whole record.
        var row = job.ToPendingRun(PostgresJobLeaseStore.RunnerProvider, DateTimeOffset.UtcNow);
        return await FixRunQueue.QueueForRunnerAsync(
            ct => leases.EnqueueCveAsync(
                row, new RunnerJobContext(job.BaseBranch, context.Prompt, context.ScopedPaths), ct),
            ct => cveFixes.TryCancelQueuedAsync(row, ct));
    }
}
