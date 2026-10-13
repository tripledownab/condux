namespace Condux.Core.FixEngine;

/// <summary>How queueing a fix run ended, and so whether the request hands its reservation back.</summary>
public enum FixQueueOutcome
{
    /// <summary>The pending run is written and its job is on its way, or the work already started.</summary>
    Queued = 0,

    /// <summary>Another run is in progress on the same work, so nothing was written or sent. The request
    /// hands its reservation back.</summary>
    InProgress = 1,

    /// <summary>Queueing failed and the run is cancelled, whether or not it had been written. Nothing can run
    /// on this reservation any more, so the request hands it back.</summary>
    Withdrawn = 2,

    /// <summary>Queueing failed and cancelling the run failed too, so whether a run exists is unknown. The
    /// request keeps the reservation, since refunding a run that then executes would hand out one nobody
    /// reserved. A run left pending is settled later: a hosted one is claimed if its job arrives or failed
    /// and refunded by the stale-run sweep, holding its issue or advisory until then, and a runner's is
    /// leased. A run that was never written, or whose cancel committed before the error, keeps its
    /// reservation until the month ends; so does one whose issue or repository was deleted meanwhile, since
    /// the cancel then fails the same way the write did.</summary>
    Unsettled = 3,
}

/// <summary>What queueing a run did, with the failure behind it when something failed.</summary>
public sealed record FixQueueResult(FixQueueOutcome Outcome, Exception? Failure = null);

/// <summary>
/// Queues a fix run, an issue fix or a CVE bump, for the hosted Conductor or for an org's runner. The run
/// is written before anything can execute it, so a second request for the same work meets it on the unique
/// index over runs in progress and is refused, rather than queued behind the first and run after it. For
/// the hosted Conductor the job is published after the write; for a runner the written row is itself the
/// delivery, so there is nothing to publish.
///
/// A failure is settled by cancelling the run, and whether the cancel took effect is the answer, because
/// neither a failed write nor a failed publish says what happened: a write can commit and still report an
/// error, or still be in flight after its client gave up, and a publish that timed out may still have
/// reached the broker. A cancelled run has nothing that can execute on its reservation, since a job that
/// arrives later meets its own run, no longer pending, and is not run, and a runner never leases a
/// cancelled run. A run already taken is executing on it, so it is queued after all.
/// </summary>
public static class FixRunQueue
{
    /// <summary>Writes the pending run, then publishes the job.</summary>
    public static async Task<FixQueueResult> QueueAsync(
        Func<CancellationToken, Task<bool>> writePending, Func<CancellationToken, Task> publish,
        Func<CancellationToken, Task<bool>> cancelPending, CancellationToken cancellationToken)
    {
        // No token on the database writes: a cancelled write could commit with the caller none the wiser.
        try
        {
            if (!await writePending(CancellationToken.None))
            {
                return new(FixQueueOutcome.InProgress);
            }
        }
        catch (Exception ex)
        {
            return await SettleAsync(cancelPending, ex);
        }

        try
        {
            await publish(cancellationToken);
            return new(FixQueueOutcome.Queued);
        }
        catch (Exception ex)
        {
            return await SettleAsync(cancelPending, ex);
        }
    }

    /// <summary>Writes a run an org's runner takes, which needs no publish.</summary>
    public static Task<FixQueueResult> QueueForRunnerAsync(
        Func<CancellationToken, Task<bool>> writePending, Func<CancellationToken, Task<bool>> cancelPending) =>
        QueueAsync(writePending, _ => Task.CompletedTask, cancelPending, CancellationToken.None);

    private static async Task<FixQueueResult> SettleAsync(
        Func<CancellationToken, Task<bool>> cancelPending, Exception failure)
    {
        bool cancelled;
        try
        {
            cancelled = await cancelPending(CancellationToken.None);
        }
        catch (Exception cancelFailure)
        {
            return new(FixQueueOutcome.Unsettled, new AggregateException(failure, cancelFailure));
        }
        return new(cancelled ? FixQueueOutcome.Withdrawn : FixQueueOutcome.Queued, failure);
    }
}
