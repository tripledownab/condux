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
        StubControlPlane server, IFixProvider provider, TimeSpan? heartbeat = null) =>
        new(server.Client(), provider,
            new RunnerOptions("test-model", TimeSpan.Zero, TimeSpan.Zero)
            {
                HeartbeatInterval = heartbeat ?? TimeSpan.FromMilliseconds(20),
            },
            NullLogger<RunnerWorker>.Instance, new ConduxSelfReporter(null));

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

        // The scoped context has to reach the provider, or the run reads the wrong files.
        Assert.Equal(Job.Prompt, provider.LastRequest!.Prompt);
        Assert.Equal(Job.ScopedPaths, provider.LastRequest.ScopedPaths);
        Assert.Equal("main", provider.LastRequest.BaseBranch);
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
