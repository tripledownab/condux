using System.Text.Json;
using Condux.Agent.ManagedAgents;
using Condux.Core.FixEngine;
using Condux.Storage.Postgres;
using Condux.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Condux.Conductor;

/// <summary>
/// Fails the hosted fix runs nothing is executing any more, and refunds their allowance. A run is left
/// in progress when the Conductor stops or crashes during it, which every deploy can do, and its job is
/// normally not delivered again. The sweep also frees a CVE bump's advisory, which accepts no second run
/// while one is in progress, though a bump GitHub cannot be asked about holds it until the pending cutoff.
///
/// A running run qualifies once it has gone unchanged for twice the provider's poll budget plus a margin.
/// The provider stops polling once that budget has passed by the clock, and the rest is for starting the
/// run, a last slow poll and the stop. A pending run waits for its job in the queue first, so it qualifies
/// a day later than that, which also recovers a job that never arrives at all. A run that outlasts even
/// that and then fails finds itself concluded and is not refunded again, because the orchestrator's
/// failure write only lands on a run in progress. Before it fails a running run it asks the source host
/// whether the run opened its pull request (<see cref="UnrecordedPullRequests"/>), and records one it finds.
///
/// With the Managed Agents backend it also removes that backend's sessions past the running cutoff, since
/// nothing polls one that old and each holds a checkout of the customer's repository at the vendor.
/// </summary>
public sealed class StaleRunSweeper(
    ILogger<StaleRunSweeper> logger, PostgresStaleRunSweep sweep, IFixStore fixes,
    ProjectEventNotifier projectEvents, ConduxSelfReporter selfReport, ManagedAgentOptions pollOptions,
    UnrecordedPullRequests pullRequests, ManagedAgentsSessionSweeper? sessions = null)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(30);

    // How long a queued job may wait for the worker beyond one run's own allowance. A conductor finishes one
    // run before it takes the next job, so a pending run waits behind the jobs ahead of it, and no bound
    // follows from the poll budget. Past this the job is taken as lost and its run is failed and refunded;
    // should it arrive after all, it finds its run no longer pending and does not run.
    private static readonly TimeSpan QueueWait = TimeSpan.FromDays(1);

    /// <summary>How long a running run may go unchanged before the sweep fails it.</summary>
    internal TimeSpan StaleAfter => (pollOptions.PollBudget * 2) + Margin;

    /// <summary>How long a pending run may wait for its job to reach the worker before the sweep fails it.</summary>
    internal TimeSpan PendingStaleAfter => StaleAfter + QueueWait;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        logger.LogInformation(
            "stale run sweep started staleAfter={StaleAfter} pendingStaleAfter={PendingStaleAfter}",
            StaleAfter, PendingStaleAfter);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "stale run sweep error");
                selfReport.Report(ex); // Condux on Condux (#75)
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>One pass. The clock is a parameter so a test can place runs either side of the cutoff.</summary>
    internal async Task SweepAsync(DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await SweepRunsAsync(now, ct);
        }
        finally
        {
            // A run sweep that throws, say on an unreachable database, must not keep sessions at the vendor.
            await SweepSessionsAsync(now, ct);
        }
    }

    private async Task SweepRunsAsync(DateTimeOffset now, CancellationToken ct)
    {
        // A running run may have opened its pull request before it stopped, so the source host is asked
        // first, and only the runs it cleared are failed before the pending cutoff.
        IReadOnlyCollection<Guid> cleared = [];
        try
        {
            cleared = await pullRequests.CheckAsync(now - StaleAfter, now, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Pending runs, and running ones past the pending cutoff, need no lookup and are still swept.
            logger.LogWarning(ex, "pull request lookup for stopped fix runs failed");
            selfReport.Report(ex);
        }
        // Once this returns, the failures and their refunds have committed and the rest is announcement,
        // so a shutdown from here on must not skip any of it: no token below.
        foreach (var run in await sweep.FailStaleAsync(now - StaleAfter, now - PendingStaleAfter, now, cleared, ct))
        {
            logger.LogWarning("interrupted fix run failed id={RunId} cveBump={IsCveBump}", run.Id, run.IsCveBump);
            if (!run.IsCveBump)
            {
                // A bump has no audit trail (ADR-0023); its row is the whole record.
                await TryAuditAsync(run.Id);
            }
            await ProjectEventNudge.TrySendAsync(projectEvents, logger, run.ProjectId, CancellationToken.None);
        }
    }

    // Its own catch, so the vendor being unreachable never surfaces as a failed pass over the runs.
    private async Task SweepSessionsAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (sessions is null)
        {
            return;
        }
        try
        {
            var result = await sessions.SweepAsync(now - StaleAfter, ct);
            if (result.Deleted + result.Stopped > 0)
            {
                logger.LogInformation(
                    "left-behind agent sessions deleted={Deleted} stopped={Stopped}", result.Deleted, result.Stopped);
            }
            if (result.Failure is { } failure)
            {
                logger.LogWarning(failure, "agent session sweep error");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "agent session sweep error");
        }
    }

    private async Task TryAuditAsync(Guid runId)
    {
        try
        {
            await fixes.AppendAuditAsync(
                runId, "conductor", "failed", JsonSerializer.Serialize(new { reason = "interrupted" }),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "interrupted fix run not audited id={RunId}", runId);
            selfReport.Report(ex);
        }
    }
}
