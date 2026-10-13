using Condux.Core.FixEngine;
using Xunit;

namespace Condux.Core.Tests;

public class ManagedAgentFixProviderTests
{
    private static readonly FixRequest Request = new("42", "acme/api", "main", "Fix issue 42.", "claude-opus-4-8")
    {
        RunId = Guid.CreateVersion7(),
    };

    // Zero interval + injected no-op delay so the poll loop never sleeps (tests stay deterministic and fast).
    private static readonly ManagedAgentOptions Fast = new(TimeSpan.Zero, MaxPolls: 5);
    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    [Fact]
    public async Task Polls_until_the_run_succeeds_then_returns_the_draft_pr()
    {
        var gateway = new ScriptedGateway("anthropic-managed-agents",
        [
            AgentRunProgress.StillRunning,
            AgentRunProgress.StillRunning,
            AgentRunProgress.Done("condux/fix-42", "https://github.com/acme/api/pull/7", "Fixed the NPE.")
                with { InputTokens = 900, OutputTokens = 210 },
        ]);
        var provider = new ManagedAgentFixProvider(gateway, Fast, NoDelay);

        var result = await provider.GenerateFixAsync(Request);

        Assert.Equal("anthropic-managed-agents", provider.Name); // provider is named after its backend
        Assert.Equal("condux/fix-42", result.Branch);
        Assert.Equal("https://github.com/acme/api/pull/7", result.PrUrl);
        Assert.Equal("Fixed the NPE.", result.Summary);
        Assert.Equal(900, result.InputTokens); // usage flows through for the cost audit
        Assert.Equal(210, result.OutputTokens);
        Assert.Equal(3, gateway.Polls);
        Assert.Equal(Request.RunId, gateway.LastSpec!.RunId); // it names the branch the gateway pushes
    }

    [Fact]
    public async Task Throws_when_the_run_fails()
    {
        // The reason the gateway recorded survives the poll, since the exception that knew it is gone.
        var gateway = new ScriptedGateway(
            "x", [AgentRunProgress.Failed("rubric not met") with { Reason = FixFailureReason.ModelUnavailable }]);
        var provider = new ManagedAgentFixProvider(gateway, Fast, NoDelay);

        var ex = await Assert.ThrowsAsync<FixFailedException>(() => provider.GenerateFixAsync(Request));
        Assert.Contains("rubric not met", ex.Message);
        Assert.Equal(FixFailureReason.ModelUnavailable, ex.Reason);
    }

    [Fact]
    public async Task Throws_when_success_reports_no_pr_url()
    {
        // A success that somehow carries no PR URL is treated as a failure, not a silent empty result.
        var gateway = new ScriptedGateway("x", [new AgentRunProgress(AgentRunStatus.Succeeded, "b", "", "", null)]);
        var provider = new ManagedAgentFixProvider(gateway, Fast, NoDelay);

        var error = await Assert.ThrowsAsync<FixFailedException>(() => provider.GenerateFixAsync(Request));
        Assert.Equal(FixFailureReason.NoUsableFix, error.Reason);
    }

    [Fact]
    public async Task Times_out_after_the_max_polls()
    {
        var gateway = new ScriptedGateway("x", [AgentRunProgress.StillRunning]); // never finishes
        var provider = new ManagedAgentFixProvider(gateway, new ManagedAgentOptions(TimeSpan.Zero, MaxPolls: 4), NoDelay);

        await Assert.ThrowsAsync<TimeoutException>(() => provider.GenerateFixAsync(Request));
        Assert.Equal(4, gateway.Polls);
    }

    [Fact]
    public async Task Slow_polls_stop_at_the_budget_by_the_clock_not_the_poll_count()
    {
        // Each poll takes a minute to answer, as a poll that times out does, against a ten minute budget
        // of 120 polls. Counting polls alone would keep the run alive for two hours.
        var clock = new SteppingClock(TimeSpan.FromMinutes(1));
        var gateway = new ScriptedGateway("managed-agents", AgentRunProgress.StillRunning) { OnPoll = clock.Advance };
        var provider = new ManagedAgentFixProvider(gateway, ManagedAgentOptions.Default, NoDelay, clock);

        await Assert.ThrowsAsync<TimeoutException>(() => provider.GenerateFixAsync(Request));
        Assert.Equal(10, gateway.Polls);
        Assert.Equal(1, gateway.Cancels);
    }

    [Fact]
    public async Task Running_out_of_polls_stops_the_run()
    {
        // The run would otherwise carry on and could open a draft PR no run records.
        var gateway = new ScriptedGateway("x", [AgentRunProgress.StillRunning]);
        var provider = new ManagedAgentFixProvider(gateway, new ManagedAgentOptions(TimeSpan.Zero, MaxPolls: 3), NoDelay);

        await Assert.ThrowsAsync<TimeoutException>(() => provider.GenerateFixAsync(Request));

        Assert.Equal(1, gateway.Cancels);
    }

    [Fact]
    public async Task A_run_that_finished_just_as_the_polls_ran_out_keeps_its_draft_pr()
    {
        // The stop found the run already done, so its PR exists and must be recorded, not orphaned.
        var gateway = new ScriptedGateway("x", [AgentRunProgress.StillRunning])
        {
            OnCancel = AgentRunProgress.Done("condux/fix-42", "https://github.com/acme/api/pull/8", "Late but done."),
        };
        var provider = new ManagedAgentFixProvider(gateway, new ManagedAgentOptions(TimeSpan.Zero, MaxPolls: 2), NoDelay);

        var result = await provider.GenerateFixAsync(Request);

        Assert.Equal("https://github.com/acme/api/pull/8", result.PrUrl);
    }

    [Fact]
    public async Task A_run_that_ends_by_itself_is_not_stopped()
    {
        var succeeded = new ScriptedGateway("x", [AgentRunProgress.Done("b", "https://github.com/acme/api/pull/1", "s")]);
        var failed = new ScriptedGateway("x", [AgentRunProgress.Failed("nope")]);

        await new ManagedAgentFixProvider(succeeded, Fast, NoDelay).GenerateFixAsync(Request);
        await Assert.ThrowsAsync<FixFailedException>(() => new ManagedAgentFixProvider(failed, Fast, NoDelay).GenerateFixAsync(Request));

        Assert.Equal(0, succeeded.Cancels);
        Assert.Equal(0, failed.Cancels);
    }

    [Fact]
    public async Task A_shutdown_mid_poll_stops_the_run_and_the_shutdown_still_surfaces()
    {
        // The caller's token has fired, so the stop must not be sent with it.
        var gateway = new ScriptedGateway("x", [AgentRunProgress.StillRunning]);
        using var shutdown = new CancellationTokenSource();
        var provider = new ManagedAgentFixProvider(gateway, Fast, (_, ct) =>
        {
            shutdown.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GenerateFixAsync(Request, shutdown.Token));

        Assert.Equal(1, gateway.Cancels);
        Assert.False(gateway.LastCancelToken.IsCancellationRequested);
    }

    [Fact]
    public async Task A_shutdown_that_finds_the_pull_request_already_opened_records_it()
    {
        // The PR exists. Rethrowing the shutdown would leave it recorded nowhere, for a retry to open a
        // second one.
        var gateway = new ScriptedGateway("x", [AgentRunProgress.StillRunning])
        {
            OnCancel = AgentRunProgress.Done("condux/fix-42", "https://github.com/acme/api/pull/5", "Done as it stopped."),
        };
        using var shutdown = new CancellationTokenSource();
        var provider = new ManagedAgentFixProvider(gateway, Fast, (_, ct) =>
        {
            shutdown.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        var result = await provider.GenerateFixAsync(Request, shutdown.Token);

        Assert.Equal("https://github.com/acme/api/pull/5", result.PrUrl);
    }

    [Fact]
    public async Task A_stop_that_fails_does_not_replace_the_error_that_ended_polling()
    {
        var gateway = new ScriptedGateway("x", [AgentRunProgress.StillRunning]) { CancelFails = true };
        var provider = new ManagedAgentFixProvider(gateway, Fast, (_, _) => throw new HttpRequestException("poll broke"));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.GenerateFixAsync(Request));

        Assert.Equal("poll broke", error.Message);
        Assert.Equal(1, gateway.Cancels);
    }

    [Fact]
    public async Task A_stop_that_fails_at_the_ceiling_still_reports_a_timeout_and_is_not_repeated()
    {
        var gateway = new ScriptedGateway("x", [AgentRunProgress.StillRunning]) { CancelFails = true };
        var provider = new ManagedAgentFixProvider(gateway, new ManagedAgentOptions(TimeSpan.Zero, MaxPolls: 2), NoDelay);

        await Assert.ThrowsAsync<TimeoutException>(() => provider.GenerateFixAsync(Request));

        Assert.Equal(1, gateway.Cancels);
    }

    [Fact]
    public async Task A_run_that_ended_for_its_own_reason_as_the_polls_ran_out_keeps_that_reason()
    {
        // The stop found the run already over, for a reason that says more than a timeout would.
        var gateway = new ScriptedGateway("x", [AgentRunProgress.StillRunning])
        {
            OnCancel = AgentRunProgress.Failed("budget reached") with { Reason = FixFailureReason.NoUsableFix },
        };
        var provider = new ManagedAgentFixProvider(gateway, new ManagedAgentOptions(TimeSpan.Zero, MaxPolls: 2), NoDelay);

        var error = await Assert.ThrowsAsync<FixFailedException>(() => provider.GenerateFixAsync(Request));

        Assert.Equal(FixFailureReason.NoUsableFix, error.Reason);
    }

    [Fact]
    public async Task A_stop_that_fails_with_a_classifiable_error_still_reports_a_timeout()
    {
        // The stop's own failure says nothing about the run, so it must not stand in for the timeout.
        var gateway = new ScriptedGateway("x", [AgentRunProgress.StillRunning])
        {
            CancelThrows = new HttpRequestException("refused", null, System.Net.HttpStatusCode.Unauthorized),
        };
        var provider = new ManagedAgentFixProvider(gateway, new ManagedAgentOptions(TimeSpan.Zero, MaxPolls: 2), NoDelay);

        await Assert.ThrowsAsync<TimeoutException>(() => provider.GenerateFixAsync(Request));
    }

    [Fact]
    public async Task Only_a_stop_for_running_out_of_time_may_publish_a_finished_answer()
    {
        // At the ceiling the run's answer may still be turned into its PR. A stop for any other reason,
        // a shutdown among them, must not start source-host writes it may not live to finish.
        var atCeiling = new ScriptedGateway("x", [AgentRunProgress.StillRunning]);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            new ManagedAgentFixProvider(atCeiling, new ManagedAgentOptions(TimeSpan.Zero, MaxPolls: 2), NoDelay)
                .GenerateFixAsync(Request));

        var onShutdown = new ScriptedGateway("x", [AgentRunProgress.StillRunning]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ManagedAgentFixProvider(onShutdown, Fast, (_, ct) => Task.FromCanceled(ct))
                .GenerateFixAsync(Request, cts.Token));

        Assert.True(atCeiling.LastPublishFinished);
        Assert.False(onShutdown.LastPublishFinished);
    }

    [Fact]
    public async Task A_ceiling_reached_after_shutdown_began_publishes_nothing()
    {
        // The loop ends on poll count and clock alone, so a shutdown can arrive at the ceiling without
        // ending polling by an exception. It must still not start source-host writes.
        var gateway = new ScriptedGateway("x", [AgentRunProgress.StillRunning]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            new ManagedAgentFixProvider(gateway, new ManagedAgentOptions(TimeSpan.Zero, MaxPolls: 2), NoDelay)
                .GenerateFixAsync(Request, cts.Token));

        Assert.False(gateway.LastPublishFinished);
    }

    // Replays a scripted sequence of progress values; once exhausted it repeats the last (so "always
    // running" is expressed by a single-element script).
    private sealed class ScriptedGateway(string backendName, params AgentRunProgress[] script) : IAgentGateway
    {
        private readonly Queue<AgentRunProgress> queue = new(script);

        public int Polls { get; private set; }

        public string BackendName => backendName;

        public AgentRunSpec? LastSpec { get; private set; }

        public Task<AgentRun> StartAsync(AgentRunSpec spec, CancellationToken cancellationToken = default)
        {
            LastSpec = spec;
            return Task.FromResult(new AgentRun("run-1"));
        }

        /// <summary>Runs on every poll, before it answers.</summary>
        public Action? OnPoll { get; init; }

        public Task<AgentRunProgress> PollAsync(string runId, CancellationToken cancellationToken = default)
        {
            Polls++;
            OnPoll?.Invoke();
            return Task.FromResult(queue.Count > 1 ? queue.Dequeue() : queue.Peek());
        }

        public int Cancels { get; private set; }

        /// <summary>What the run reports when it is stopped: unfinished by default.</summary>
        public AgentRunProgress OnCancel { get; init; } = AgentRunProgress.Failed("stopped");

        public bool CancelFails { get; init; }

        /// <summary>What a failing stop throws, when it should be something other than the default.</summary>
        public Exception? CancelThrows { get; init; }

        public CancellationToken LastCancelToken { get; private set; }

        public bool? LastPublishFinished { get; private set; }

        public Task<AgentRunProgress> CancelAsync(string runId, CancellationToken cancellationToken = default, bool publishFinished = false)
        {
            Cancels++;
            LastCancelToken = cancellationToken;
            LastPublishFinished = publishFinished;
            return CancelThrows is { } thrown ? throw thrown
                : CancelFails ? throw new InvalidOperationException("the stop itself failed")
                : Task.FromResult(OnCancel);
        }
    }

    // A clock that stands still until told to move, so a test decides how long each poll takes.
    private sealed class SteppingClock(TimeSpan step) : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public void Advance() => now += step;

        public override DateTimeOffset GetUtcNow() => now;
    }
}
