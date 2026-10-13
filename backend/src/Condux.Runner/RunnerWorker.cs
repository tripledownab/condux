using System.Diagnostics;
using Condux.Core.FixEngine;
using Condux.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Condux.Runner;

/// <summary>How the poll loop paces itself. Injected so tests drive it without ever sleeping.</summary>
public sealed record RunnerOptions(string Model, TimeSpan IdlePoll, TimeSpan ErrorBackoff)
{
    /// <summary>A third of the lease, so two heartbeats can be lost before the job is considered
    /// abandoned. Extending on the last possible moment would hand work away on one slow request.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = JobLease.Duration / 3;

    /// <summary>How long the control plane holds a lease without a heartbeat. A setting only so a test
    /// can shorten it: the control plane decides the real value.</summary>
    public TimeSpan LeaseDuration { get; init; } = JobLease.Duration;

    /// <summary>When the work stops, counted from the SEND of the last heartbeat that landed: a fifth of a
    /// lease early. The control plane extends the lease no earlier than the send, so stopping first keeps
    /// this runner from working past a lapse, after which the run may be cancelled and refunded or handed
    /// to another runner. Two heartbeats can still fail first.</summary>
    public TimeSpan WorkDeadline => LeaseDuration - LeaseDuration / 5;
}

/// <summary>
/// The runner loop (ADR-0033 slice 4, #68): lease a job, run it here on the customer's compute with the
/// customer's own model and source-host credentials, report the outcome back. Condux keeps orchestration,
/// metering and the audit trail; the code and the keys never leave the customer's boundary.
///
/// The execution itself is the same <see cref="IFixProvider"/> the hosted Conductor runs, so a fix behaves
/// identically in both deployments. What differs is only where the work comes from and where it goes.
/// </summary>
public sealed class RunnerWorker(
    LeaseClient leases, IFixProvider provider, RunnerOptions options,
    ILogger<RunnerWorker> logger, ConduxSelfReporter selfReport)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("runner polling for work model={Model}", options.Model);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await leases.TryLeaseAsync(stoppingToken);
                if (job is null)
                {
                    await Task.Delay(options.IdlePoll, stoppingToken);
                    continue;
                }

                await RunAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The control plane being unreachable is expected on a customer's network, so it backs off
                // and keeps polling rather than exiting: a runner that stops on the first blip needs a
                // human to restart it, and nobody is watching it.
                logger.LogWarning(ex, "runner poll failed");
                selfReport.Report(ex);
                await Task.Delay(options.ErrorBackoff, stoppingToken);
            }
        }
    }

    /// <summary>One job, start to reported outcome. Internal so the behaviour that matters — refusing a
    /// context-less job, and stopping the moment the lease is lost — is testable without racing the loop.
    /// </summary>
    internal async Task RunAsync(RunnerJob job, CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(job.Prompt))
        {
            // Refused rather than run with an invented prompt. A fix that read nothing still produces a
            // confident patch and still bills the customer for the model call, so failing is the honest
            // outcome and the allowance is refunded.
            logger.LogWarning("job {FixId} carried no fix context", job.FixId);
            await ReportAsync(
                job, FixStatus.Failed, result: null, "The job carried no fix context.",
                FixFailureReason.Unknown, stoppingToken);
            return;
        }

        logger.LogInformation("running fix {FixId} repo={Repo}", job.FixId, job.RepoFullName);

        // Cancelled when the host stops, the lease is lost, or no heartbeat has landed for long enough that
        // it may lapse. All three mean stop: another runner may take the job, and one fix opens two pull
        // requests. The deadline fires on time even while a heartbeat hangs; the lease was granted before
        // this runner heard of it, and the margin in WorkDeadline covers that gap.
        using var deadline = new CancellationTokenSource(options.WorkDeadline);
        using var work = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, deadline.Token);
        var heartbeat = KeepLeaseAsync(job, work, deadline);

        try
        {
            var request = new FixRequest(
                job.WorkRef, job.RepoFullName, job.BaseBranch, job.Prompt, options.Model)
            { RunId = job.FixId, ScopedPaths = job.ScopedPaths };
            var result = await provider.GenerateFixAsync(request, work.Token);
            await ReportAsync(job, FixStatus.Succeeded, result, "", failureReason: null, stoppingToken);
            logger.LogInformation("fix {FixId} opened {PrUrl}", job.FixId, result.PrUrl);
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            // The lease was lost mid-run, or is about to be. Whoever holds it next reports; a report from
            // here is refused once it lapses, and logging a failure would describe one that never happened.
            logger.LogWarning(
                deadline.IsCancellationRequested
                    ? "no heartbeat on {FixId} landed in time, stopping before the lease can lapse"
                    : "lost the lease on {FixId} while running it",
                job.FixId);
        }
        // A cancellation nobody here asked for is a timeout inside the run, which fails it like any other
        // error. Reading it as a lost lease would report nothing and let the job be run a second time.
        // ReportAsync never throws short of shutdown, so a success report cannot land here.
        catch (Exception ex) when (ex is not OperationCanceledException || !work.IsCancellationRequested)
        {
            logger.LogWarning(ex, "fix {FixId} failed", job.FixId);
            selfReport.Report(ex);
            await ReportAsync(job, FixStatus.Failed, result: null, ex.Message, FixFailures.Classify(ex), stoppingToken);
        }
        finally
        {
            await work.CancelAsync();
            await heartbeat;
        }
    }

    /// <summary>
    /// Report the outcome, retrying a transport failure. This is the one call worth retrying: by the time
    /// it runs the draft pull request already exists, and a report that never lands leaves the lease to
    /// lapse and the fix to be run again, opening a second pull request for it.
    ///
    /// A refusal is not retried. False means the lease was lost, so the answer will never change, and
    /// whoever holds the job now is the one entitled to report on it.
    /// </summary>
    private async Task ReportAsync(
        RunnerJob job, FixStatus status, FixResult? result, string summary, FixFailureReason? failureReason,
        CancellationToken cancellationToken)
    {
        const int attempts = 3;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (!await leases.ReportAsync(job, status, result, options.Model, summary, failureReason, cancellationToken))
                {
                    logger.LogWarning("report on {FixId} refused: the lease is no longer ours", job.FixId);
                }

                return;
            }
            // A timeout is a transport failure like any other and is retried. Letting it escape would turn a
            // success report into a Failed one, for a run whose pull request already exists.
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex, "reporting {FixId} failed, attempt {Attempt} of {Attempts}", job.FixId, attempt, attempts);
                if (attempt == attempts)
                {
                    // Give up rather than rethrow. Throwing from the success path would be caught as a
                    // failed run and report the opposite of what happened; letting the lease lapse instead
                    // is the honest at-least-once behaviour of a protocol over a network we do not own.
                    return;
                }

                await Task.Delay(options.ErrorBackoff, cancellationToken);
            }
        }
    }

    /// <summary>Hold the lease while the work runs: cancel it when the lease is lost, push the deadline out
    /// when a heartbeat lands.</summary>
    private async Task KeepLeaseAsync(RunnerJob job, CancellationTokenSource work, CancellationTokenSource deadline)
    {
        // A local stopwatch, not the lease's expiry time, so a skewed customer clock cannot move it.
        var clock = Stopwatch.StartNew();
        try
        {
            while (!work.IsCancellationRequested)
            {
                await Task.Delay(options.HeartbeatInterval, work.Token);
                var sentAt = clock.Elapsed;
                bool held;
                try
                {
                    held = await leases.HeartbeatAsync(job, work.Token);
                }
                // A heartbeat that times out or cannot connect is not a lost lease; the next one asks again,
                // and the deadline stops the work if none lands in time, without waiting for this call.
                catch (Exception ex) when (ex is not OperationCanceledException || !work.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "heartbeat on {FixId} failed, retrying", job.FixId);
                    continue;
                }

                if (!held)
                {
                    await work.CancelAsync();
                    return;
                }

                var left = options.WorkDeadline - (clock.Elapsed - sentAt); // counted from the send
                deadline.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            }
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            // The run finished, the host is stopping, or the deadline passed. Nothing left to hold.
        }
    }
}
