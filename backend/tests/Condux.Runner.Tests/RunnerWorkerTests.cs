using System.Diagnostics;
using Condux.Core.FixEngine;
using Condux.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Condux.Runner.Tests;

public class RunnerWorkerTests
{
    private static readonly RunnerJob Job = new(
        Guid.CreateVersion7(), 42, "acme/api", "main", "Fix the ValueError in checkout.",
        ["src/checkout.py"], DateTimeOffset.UtcNow.AddMinutes(5), "lease-1");

    /// <summary>Records whether it ran, and can be told to block until its run is cancelled.</summary>
    private sealed class RecordingProvider(FixResult? result = null, bool blockForever = false) : IFixProvider
    {
        public string Name => "recording";

        public bool WasCalled { get; private set; }

        public FixRequest? LastRequest { get; private set; }

        public async Task<FixResult> GenerateFixAsync(
            FixRequest request, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            LastRequest = request;
            if (blockForever)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            return result ?? new FixResult("fix/x", "https://host/pr/1", "done");
        }
    }

    private static RunnerWorker Worker(
        StubControlPlane server, IFixProvider provider, TimeSpan? heartbeat = null, TimeSpan? lease = null) =>
        new(server.Client(), provider,
            new RunnerOptions("test-model", TimeSpan.Zero, TimeSpan.Zero)
            {
                HeartbeatInterval = heartbeat ?? TimeSpan.FromMilliseconds(20),
                LeaseDuration = lease ?? TimeSpan.FromMinutes(5),
            },
            NullLogger<RunnerWorker>.Instance, new ConduxSelfReporter(null));

    [Fact]
    public async Task A_timed_out_poll_is_retried_rather_than_read_as_shutdown()
    {
        // A runner on a customer's network has nobody watching it, so a timeout that ended its loop
        // would stop it taking work until someone noticed and restarted it.
        var server = new StubControlPlane { TimeOutLeasesBeforeAnswering = 1 };
        var worker = Worker(server, new RecordingProvider());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await server.LeaseAnswered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_timed_out_heartbeat_is_retried_and_the_run_keeps_its_lease()
    {
        // The run finishes only once a later heartbeat lands. A heartbeat loop that ended at the first
        // timeout would never get there, and the lease would lapse under the run.
        var server = new StubControlPlane { FailHeartbeatsBeforeAnswering = 1 };
        var provider = new WaitingProvider(server.HeartbeatAnswered.Task);

        await Worker(server, provider).RunAsync(Job, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var report = Assert.Single(server.Reports);
        Assert.Contains($"\"status\":{(int)FixStatus.Succeeded}", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_model_call_that_times_out_is_reported_failed_not_read_as_a_lost_lease()
    {
        // Read as a lost lease, the run reported nothing, so the lease lapsed and the job ran again.
        var server = new StubControlPlane();
        var provider = new WaitingProvider(Task.FromException(new TaskCanceledException("timed out")));

        await Worker(server, provider).RunAsync(Job, CancellationToken.None);

        var report = Assert.Single(server.Reports);
        Assert.Contains($"\"status\":{(int)FixStatus.Failed}", report, StringComparison.Ordinal);
        // The runner says why, so a failed run on the customer's compute is explained like a hosted one.
        Assert.Contains($"\"failureReason\":{(int)FixFailureReason.TimedOut}", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_heartbeat_the_edge_answers_with_503_is_retried_not_read_as_a_lost_lease()
    {
        // Only 409, or 401 for a revoked token, means the lease is gone. A 503 from the edge while the
        // control plane redeploys used to abandon the run as if it were.
        var server = new StubControlPlane
        {
            FailHeartbeatsBeforeAnswering = 1,
            HeartbeatFailure = () => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable),
        };
        var provider = new WaitingProvider(server.HeartbeatAnswered.Task);

        await Worker(server, provider).RunAsync(Job, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var report = Assert.Single(server.Reports);
        Assert.Contains($"\"status\":{(int)FixStatus.Succeeded}", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_success_report_that_times_out_is_retried_and_never_reported_as_a_failure()
    {
        // The pull request exists by now, so the report must land as a success. A timed-out report that
        // escaped would be caught as a failed run and reported Failed, refunding an allowance that was spent.
        var server = new StubControlPlane
        {
            FailReportsBeforeAccepting = 1,
            ReportFailure = () => throw new TaskCanceledException("timed out"),
        };

        await Worker(server, new RecordingProvider()).RunAsync(Job, CancellationToken.None);

        var report = Assert.Single(server.Reports);
        Assert.Contains($"\"status\":{(int)FixStatus.Succeeded}", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_report_the_edge_answers_with_502_is_retried_not_read_as_refused()
    {
        // Only 409, or 401 for a revoked token, is a refusal. Dropping a success report on a 502 during a
        // redeploy let the lease lapse and the job run again, opening a second pull request.
        var server = new StubControlPlane
        {
            FailReportsBeforeAccepting = 1,
            ReportFailure = () => new HttpResponseMessage(System.Net.HttpStatusCode.BadGateway),
        };

        await Worker(server, new RecordingProvider()).RunAsync(Job, CancellationToken.None);

        var report = Assert.Single(server.Reports);
        Assert.Contains($"\"status\":{(int)FixStatus.Succeeded}", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Heartbeats_that_fail_for_a_whole_lease_stop_the_run()
    {
        // The lease has lapsed on the control plane by then, and another runner may hold the job, so
        // retrying for ever would be two runs on one issue.
        var server = new StubControlPlane
        {
            FailHeartbeatsBeforeAnswering = int.MaxValue,
            HeartbeatFailure = () => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable),
        };
        var provider = new RecordingProvider(blockForever: true);

        await Worker(server, provider, lease: TimeSpan.FromMilliseconds(100))
            .RunAsync(Job, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(server.Reports);
    }

    [Fact]
    public async Task Heartbeats_that_hang_still_stop_the_run()
    {
        // A partition leaves a heartbeat call hanging until its own timeout, and the stop used to be
        // decided only after a call returned, so the run went on working past the lease's lapse.
        var server = new StubControlPlane { AnswerHeartbeatsThenHang = 0 };
        var provider = new RecordingProvider(blockForever: true);

        await Worker(server, provider, lease: TimeSpan.FromMilliseconds(300))
            .RunAsync(Job, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(provider.WasCalled);
        Assert.Empty(server.Reports);
    }

    [Fact]
    public async Task The_run_stops_before_a_whole_lease_passes_since_the_last_heartbeat_that_landed()
    {
        // After a lapse the control plane may cancel and refund the run, or hand it to another runner, so a
        // runner still working then can open a draft PR recorded nowhere. The stop is counted from the last
        // heartbeat that landed and fires a margin early, rather than a whole lease after its answer.
        var server = new StubControlPlane { AnswerHeartbeatsThenHang = 1 };
        var provider = new CancelTimingProvider(server.Clock);
        var lease = TimeSpan.FromSeconds(3);

        await Worker(server, provider, lease: lease)
            .RunAsync(Job, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(1, server.HeartbeatCount);
        // The deadline is four fifths of a lease; the rest of the bound is slack for a loaded machine,
        // and still well inside the lease, so a stop with no margin fails here.
        var sinceLanded = provider.CancelledAt - server.LastAnsweredHeartbeatAt;
        Assert.True(sinceLanded < lease * 0.9, $"stopped {sinceLanded} after the last heartbeat landed, lease {lease}");
        Assert.Empty(server.Reports);
    }

    /// <summary>Runs until cancelled and records when, on the stub's clock.</summary>
    private sealed class CancelTimingProvider(System.Diagnostics.Stopwatch clock) : IFixProvider
    {
        public string Name => "cancel-timing";

        public TimeSpan CancelledAt { get; private set; }

        public async Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                CancelledAt = clock.Elapsed;
            }

            throw new UnreachableException();
        }
    }

    [Fact]
    public async Task Heartbeats_that_keep_landing_between_failures_hold_the_lease_for_a_long_run()
    {
        // Only a whole lease with no heartbeat landing stops the run, counted from the last one that did.
        var server = new StubControlPlane
        {
            FailEveryOtherHeartbeat = true,
            HeartbeatFailure = () => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable),
        };
        // Margins wide enough that a slow machine stretching the 20ms heartbeat does not fail it wrongly.
        var provider = new WaitingProvider(Task.Delay(TimeSpan.FromMilliseconds(1500)));

        await Worker(server, provider, lease: TimeSpan.FromMilliseconds(400))
            .RunAsync(Job, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        var report = Assert.Single(server.Reports);
        Assert.Contains($"\"status\":{(int)FixStatus.Succeeded}", report, StringComparison.Ordinal);
    }

    /// <summary>Finishes when <paramref name="gate"/> does, failing with it if it fails.</summary>
    private sealed class WaitingProvider(Task gate) : IFixProvider
    {
        public string Name => "waiting";

        public async Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
        {
            await gate.WaitAsync(cancellationToken);
            return new FixResult("fix/x", "https://host/pr/1", "done");
        }
    }

    [Fact]
    public async Task No_work_is_not_an_error()
    {
        // A runner polls constantly and most polls find nothing. If 204 read as a failure the runner would
        // spend its life backing off from its own healthy queue.
        var server = new StubControlPlane();

        Assert.Null(await server.Client().TryLeaseAsync());
    }

    [Fact]
    public async Task A_job_with_no_context_fails_without_calling_the_model()
    {
        // Running it anyway would send the model an invented prompt, produce a confident patch off no
        // evidence, and bill the customer for it. Failing refunds the allowance instead.
        var server = new StubControlPlane();
        var provider = new RecordingProvider();

        await Worker(server, provider).RunAsync(Job with { Prompt = "  " }, CancellationToken.None);

        Assert.False(provider.WasCalled);
        var report = Assert.Single(server.Reports);
        Assert.Contains($"\"status\":{(int)FixStatus.Failed}", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_finished_run_reports_the_pull_request_and_the_model_that_produced_it()
    {
        var server = new StubControlPlane();
        var provider = new RecordingProvider(
            new FixResult("fix/checkout", "https://host/pr/7", "fixed it") { InputTokens = 900, OutputTokens = 120 });

        await Worker(server, provider).RunAsync(Job, CancellationToken.None);

        var report = Assert.Single(server.Reports);
        Assert.Contains("https://host/pr/7", report, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"test-model\"", report, StringComparison.Ordinal);
        Assert.Contains("\"inputTokens\":900", report, StringComparison.Ordinal);
        Assert.Contains($"\"status\":{(int)FixStatus.Succeeded}", report, StringComparison.Ordinal);
        Assert.Contains("\"failureReason\":null", report, StringComparison.Ordinal);

        // The scoped context has to reach the provider, or the run reads the wrong files.
        Assert.Equal(Job.Prompt, provider.LastRequest!.Prompt);
        Assert.Equal(Job.ScopedPaths, provider.LastRequest.ScopedPaths);
        Assert.Equal("main", provider.LastRequest.BaseBranch);
        // The leased row's id names the branch, as on the hosted path.
        Assert.Equal(Job.FixId, provider.LastRequest.RunId);
        // A server that predates the ref field sends none, so the issue id names the work.
        Assert.Equal("42", provider.LastRequest.IssueId);
    }

    [Fact]
    public async Task The_servers_ref_names_the_work_when_present()
    {
        // A CVE bump has no issue number — its lease carries the advisory id as the ref, and that is what
        // must reach the provider's branch and pull-request naming. Falling back to IssueId here would put
        // "fix-0" on a customer's repo.
        var server = new StubControlPlane();
        var provider = new RecordingProvider();

        await Worker(server, provider)
            .RunAsync(Job with { IssueId = 0, Ref = "GHSA-jf85-cpcp-j695" }, CancellationToken.None);

        Assert.Equal("GHSA-jf85-cpcp-j695", provider.LastRequest!.IssueId);
    }

    [Fact]
    public async Task A_failed_run_is_reported_rather_than_left_holding_the_lease()
    {
        // Silence would strand the fix until the lease lapsed, then hand it to another runner to fail the
        // same way. Reporting ends it and refunds the allowance.
        var server = new StubControlPlane();

        await Worker(server, new ThrowingProvider()).RunAsync(Job, CancellationToken.None);

        var report = Assert.Single(server.Reports);
        Assert.Contains($"\"status\":{(int)FixStatus.Failed}", report, StringComparison.Ordinal);
        Assert.Contains("provider exploded", report, StringComparison.Ordinal);
    }

    private sealed class ThrowingProvider : IFixProvider
    {
        public string Name => "throwing";

        public Task<FixResult> GenerateFixAsync(FixRequest r, CancellationToken ct = default) =>
            throw new InvalidOperationException("provider exploded");
    }

    [Fact]
    public async Task A_dropped_report_is_retried_rather_than_losing_a_pull_request_that_exists()
    {
        // By the time the report goes out the draft pull request is already open. A report that never
        // lands leaves the lease to lapse, another runner takes the job, and the issue gets a second
        // pull request for one fix.
        var server = new StubControlPlane { FailReportsBeforeAccepting = 2 };

        await Worker(server, new RecordingProvider()).RunAsync(Job, CancellationToken.None);

        var report = Assert.Single(server.Reports);
        Assert.Contains($"\"status\":{(int)FixStatus.Succeeded}", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_report_that_cannot_be_delivered_at_all_does_not_invert_the_outcome()
    {
        // The trap in retrying: throwing out of the success path lands in the failure handler, which
        // would report Failed for a run that opened a pull request. Giving up and letting the lease
        // lapse is wrong-but-recoverable; reporting the opposite of what happened is neither.
        var server = new StubControlPlane { FailReportsBeforeAccepting = int.MaxValue };

        await Worker(server, new RecordingProvider()).RunAsync(Job, CancellationToken.None);

        Assert.Empty(server.Reports);
    }

    [Fact]
    public async Task Losing_the_lease_stops_the_work_instead_of_finishing_it()
    {
        // The reason the heartbeat cancels rather than just logs: another runner already holds this job,
        // so carrying on means two agents on one issue and two draft pull requests. Reporting afterwards
        // would also overwrite the outcome of the runner that actually finished.
        var server = new StubControlPlane { LeaseHeld = false };
        var provider = new RecordingProvider(blockForever: true);

        // Would hang forever if the lost lease did not cancel the run, so the test failing looks like a
        // timeout rather than a wrong assertion.
        await Worker(server, provider).RunAsync(Job, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(provider.WasCalled);
        Assert.Empty(server.Reports);
    }

    [Fact]
    public async Task A_long_run_keeps_its_lease_alive()
    {
        // The mirror of the case above: silence is what makes a job reclaimable, so a healthy run that
        // simply takes a while must keep saying so or it loses work it is doing correctly.
        var server = new StubControlPlane();
        var slow = new SlowProvider();

        await Worker(server, slow).RunAsync(Job, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(server.HeartbeatCount >= 2, $"expected repeated heartbeats, saw {server.HeartbeatCount}");
        Assert.Single(server.Reports);
    }

    private sealed class SlowProvider : IFixProvider
    {
        public string Name => "slow";

        public async Task<FixResult> GenerateFixAsync(FixRequest r, CancellationToken ct = default)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120), ct);
            return new FixResult("fix/x", "https://host/pr/2", "done");
        }
    }
}
