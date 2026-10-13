using System.Collections.Concurrent;
using Condux.Core.FixEngine;

namespace Condux.Agent;

/// <summary>
/// The bookkeeping behind an in-process gateway's start, poll and stop seam: each run is a background
/// task with its own stop, linked to the caller's token so one run can be stopped without stopping the
/// worker. What a run does is the gateway's business; how it is started, watched and stopped is this.
/// </summary>
internal sealed class InProcessRuns(TimeSpan? stopWait = null)
{
    // How long a stop waits for the run to reach a cancellation check. A run passes its token to the
    // calls it makes, so this is a ceiling for one stuck somewhere that does not, not an expected wait.
    private readonly TimeSpan stopWait = stopWait ?? TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, AgentRunProgress> runs = new();

    // The runs still in flight, each with the source that stops it and the task to wait on.
    private readonly ConcurrentDictionary<string, (CancellationTokenSource Stop, Task Work)> active = new();

    /// <summary>Start a run in the background and return its id. It outlives this call by design, and a
    /// failure lands in its state rather than as an unobserved exception.</summary>
    public string Start(Func<CancellationToken, Task<AgentRunProgress>> run, CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid().ToString("n");
        runs[runId] = AgentRunProgress.StillRunning;

        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var work = Task.Run(async () =>
        {
            // Written only while the run is still tracked: one that outlived its stop's wait is no longer,
            // and writing it back would keep a state nobody reads for the life of the process.
            AgentRunProgress outcome;
            try
            {
                outcome = await run(stop.Token);
            }
            catch (Exception ex)
            {
                outcome = AgentRunProgress.Failed(ex);
            }
            runs.TryUpdate(runId, outcome, AgentRunProgress.StillRunning);
        });
        active[runId] = (stop, work);
        _ = work.ContinueWith(_ =>
        {
            active.TryRemove(runId, out var _);
            stop.Dispose();
        }, TaskScheduler.Default);
        return runId;
    }

    public AgentRunProgress Poll(string runId) =>
        runs.TryGetValue(runId, out var progress)
            ? progress
            : throw new KeyNotFoundException($"Unknown agent run '{runId}'.");

    /// <summary>Stop a run and wait, up to a bound, for it to settle, so it makes no further calls once
    /// this returns. A run stuck past the bound is left running and reported unfinished. Nobody polls a
    /// stopped run, so its state is dropped here rather than kept for ever.</summary>
    public async Task<AgentRunProgress> StopAsync(string runId, CancellationToken cancellationToken)
    {
        if (active.TryGetValue(runId, out var run))
        {
            try
            {
                await run.Stop.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // The run finished and released its source between the lookup and the cancel.
            }
            try
            {
                await run.Work.WaitAsync(stopWait, cancellationToken);
            }
            catch (TimeoutException)
            {
                // Still running past the ceiling. Reported as not finished, below.
            }
        }

        return runs.TryRemove(runId, out var progress) && progress.Status != AgentRunStatus.Running
            ? progress
            : AgentRunProgress.Failed(new TimeoutException("The run was stopped before it finished."));
    }
}
