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
            catch (OperationCanceledException)
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
                job, FixStatus.Failed, result: null, "The job carried no fix context.", stoppingToken);
            return;
        }

        logger.LogInformation("running fix {FixId} repo={Repo}", job.FixId, job.RepoFullName);

        // Cancelled either because the host is shutting down or because the lease was lost. Both mean stop:
        // continuing after another runner has taken the job is how one fix opens two pull requests.
        using var work = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var heartbeat = KeepLeaseAsync(job, work);

        try
        {
            var request = new FixRequest(
                job.WorkRef, job.RepoFullName, job.BaseBranch, job.Prompt, options.Model)
            {
                ScopedPaths = job.ScopedPaths,
            };
            var result = await provider.GenerateFixAsync(request, work.Token);
            await ReportAsync(job, FixStatus.Succeeded, result, "", stoppingToken);
            logger.LogInformation("fix {FixId} opened {PrUrl}", job.FixId, result.PrUrl);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // The lease lapsed mid-run. Whoever holds it now will report; a report from here is refused
            // anyway, and pretending otherwise would log a failure that never happened.
            logger.LogWarning("lost the lease on {FixId} while running it", job.FixId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "fix {FixId} failed", job.FixId);
            selfReport.Report(ex);
            await ReportAsync(job, FixStatus.Failed, result: null, ex.Message, stoppingToken);
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
        RunnerJob job, FixStatus status, FixResult? result, string summary, CancellationToken cancellationToken)
    {
        const int attempts = 3;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (!await leases.ReportAsync(job, status, result, options.Model, summary, cancellationToken))
                {
                    logger.LogWarning("report on {FixId} refused: the lease is no longer ours", job.FixId);
                }

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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

    /// <summary>Hold the lease for as long as the work runs, and cancel the work the moment it is lost.</summary>
    private async Task KeepLeaseAsync(RunnerJob job, CancellationTokenSource work)
    {
        try
        {
            while (!work.IsCancellationRequested)
            {
                await Task.Delay(options.HeartbeatInterval, work.Token);
                if (!await leases.HeartbeatAsync(job, work.Token))
                {
                    await work.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The run finished, or the host is stopping. Either way there is nothing left to hold.
        }
    }
}
