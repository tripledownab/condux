using Condux.Agent;
using Condux.Core.FixEngine;
using Xunit;

namespace Condux.Conductor.Tests;

public class InProcessRunsTests
{
    [Fact]
    public async Task A_run_that_outlives_its_stop_is_not_tracked_again_when_it_finishes()
    {
        // The stop waits only so long. A run still going after that finishes later, and writing its state
        // back then would keep it for the life of the process, with nothing left to read or remove it.
        var runs = new InProcessRuns(stopWait: TimeSpan.FromMilliseconds(50));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runId = runs.Start(async _ =>
        {
            await release.Task; // a step that does not observe the stop
            finished.SetResult();
            return AgentRunProgress.Done("b", "https://github.com/acme/api/pull/1", "late");
        }, CancellationToken.None);

        var stopped = await runs.StopAsync(runId, CancellationToken.None);
        release.SetResult();
        await finished.Task;

        Assert.Equal(AgentRunStatus.Failed, stopped.Status);
        // The write lands just after the delegate returns, so give it a bounded chance to appear.
        for (var i = 0; i < 50; i++)
        {
            Assert.Throws<KeyNotFoundException>(() => runs.Poll(runId));
            await Task.Delay(10);
        }
    }
}
