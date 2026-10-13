using Condux.Core.FixEngine;
using Xunit;

namespace Condux.Core.Tests;

/// <summary>
/// Queueing a hosted fix request: the pending run is written before the job is published, and a failure is
/// settled by cancelling that run, whose result says whether the request hands its reservation back. The
/// outcome is the only thing the callers read to decide that, so a wrong one either keeps a reservation for
/// a run that never happens or refunds one that still runs.
/// </summary>
public class FixRunQueueTests
{
    private readonly List<string> _calls = [];

    private Task<FixQueueResult> QueueAsync(
        bool written = true, Exception? writeFailure = null, Exception? publishFailure = null,
        bool cancelTakesEffect = true, Exception? cancelFailure = null, CancellationToken cancellationToken = default) =>
        FixRunQueue.QueueAsync(
            _ =>
            {
                _calls.Add("write");
                return writeFailure is null ? Task.FromResult(written) : throw writeFailure;
            },
            _ =>
            {
                _calls.Add("publish");
                return publishFailure is null ? Task.CompletedTask : throw publishFailure;
            },
            _ =>
            {
                _calls.Add("cancel");
                return cancelFailure is null ? Task.FromResult(cancelTakesEffect) : throw cancelFailure;
            },
            cancellationToken);

    [Fact]
    public async Task Writes_the_pending_run_before_it_publishes()
    {
        var result = await QueueAsync();

        Assert.Equal(FixQueueOutcome.Queued, result.Outcome);
        Assert.Equal(["write", "publish"], _calls);
    }

    [Fact]
    public async Task A_run_in_progress_on_the_same_work_is_refused_and_nothing_is_published()
    {
        var result = await QueueAsync(written: false);

        Assert.Equal(FixQueueOutcome.InProgress, result.Outcome);
        Assert.Equal(["write"], _calls);
    }

    [Fact]
    public async Task A_write_that_fails_is_cancelled_and_withdrawn()
    {
        // Nothing was published, and the cancel leaves a cancelled run whether or not the write landed, so
        // nothing can run.
        var failure = new InvalidOperationException("connection reset");

        var result = await QueueAsync(writeFailure: failure);

        Assert.Equal(FixQueueOutcome.Withdrawn, result.Outcome);
        Assert.Same(failure, result.Failure);
        Assert.Equal(["write", "cancel"], _calls);
    }

    [Fact]
    public async Task A_failed_publish_cancels_the_pending_run_and_is_withdrawn()
    {
        // Including a delivery timeout that may have reached the broker: the job, if it arrives, meets its
        // own run cancelled and does not run, so the reservation can go back.
        var failure = new InvalidOperationException("delivery timed out");

        var result = await QueueAsync(publishFailure: failure);

        Assert.Equal(FixQueueOutcome.Withdrawn, result.Outcome);
        Assert.Same(failure, result.Failure);
        Assert.Equal(["write", "publish", "cancel"], _calls);
    }

    [Fact]
    public async Task A_publish_cancelled_by_the_caller_is_settled_the_same_way()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await QueueAsync(
            publishFailure: new OperationCanceledException(cancelled.Token), cancellationToken: cancelled.Token);

        Assert.Equal(FixQueueOutcome.Withdrawn, result.Outcome);
        Assert.Equal(["write", "publish", "cancel"], _calls);
    }

    [Fact]
    public async Task A_failed_publish_whose_run_the_conductor_already_claimed_is_queued()
    {
        // The cancel found the run no longer pending: the job arrived after all and is running on this
        // reservation, so it must not be handed back.
        var result = await QueueAsync(
            publishFailure: new InvalidOperationException("ack lost"), cancelTakesEffect: false);

        Assert.Equal(FixQueueOutcome.Queued, result.Outcome);
    }

    [Fact]
    public async Task A_run_that_cannot_be_cancelled_keeps_its_reservation()
    {
        // The run may still be pending, and then the sweep refunds it later. Refunding here as well would
        // hand the run back twice.
        var result = await QueueAsync(
            publishFailure: new InvalidOperationException("broker down"),
            cancelFailure: new InvalidOperationException("database down"));

        Assert.Equal(FixQueueOutcome.Unsettled, result.Outcome);
        Assert.IsType<AggregateException>(result.Failure);
        Assert.Equal(["write", "publish", "cancel"], _calls);
    }

    [Fact]
    public async Task A_failed_write_whose_cancel_fails_too_keeps_its_reservation()
    {
        // Whether the write landed is unknown, and refunding a run that then executes would hand out one
        // nobody reserved.
        var result = await QueueAsync(
            writeFailure: new InvalidOperationException("connection reset"),
            cancelFailure: new InvalidOperationException("database down"));

        Assert.Equal(FixQueueOutcome.Unsettled, result.Outcome);
        Assert.Equal(["write", "cancel"], _calls);
    }

    [Fact]
    public async Task A_runners_run_is_queued_by_its_write_alone()
    {
        var calls = new List<string>();

        var result = await FixRunQueue.QueueForRunnerAsync(
            _ => { calls.Add("write"); return Task.FromResult(true); },
            _ => { calls.Add("cancel"); return Task.FromResult(true); });

        Assert.Equal(FixQueueOutcome.Queued, result.Outcome);
        Assert.Equal(["write"], calls);
    }

    [Fact]
    public async Task A_runners_insert_that_fails_is_cancelled_before_it_is_refunded()
    {
        // An insert can commit and still report an error, and a runner would lease that row and run it on
        // a refunded reservation. Cancelled, it is no longer pending, so no runner leases it.
        var calls = new List<string>();

        var result = await FixRunQueue.QueueForRunnerAsync(
            _ => { calls.Add("write"); throw new InvalidOperationException("connection reset"); },
            _ => { calls.Add("cancel"); return Task.FromResult(true); });

        Assert.Equal(FixQueueOutcome.Withdrawn, result.Outcome);
        Assert.Equal(["write", "cancel"], calls);
    }

    [Fact]
    public async Task A_runners_insert_that_fails_but_was_already_leased_is_queued()
    {
        // The insert committed, reported an error, and a runner leased the row before the cancel: it is
        // running on this reservation, so it must not be handed back.
        var result = await FixRunQueue.QueueForRunnerAsync(
            _ => throw new InvalidOperationException("connection reset"),
            _ => Task.FromResult(false));

        Assert.Equal(FixQueueOutcome.Queued, result.Outcome);
    }
}
