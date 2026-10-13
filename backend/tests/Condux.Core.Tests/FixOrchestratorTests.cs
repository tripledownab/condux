using System.Text.Json;
using Condux.Core.FixEngine;
using Xunit;

namespace Condux.Core.Tests;

public class FixOrchestratorTests
{
    private static readonly FixJob Job = new(42, "acme/api", "main", "dev@example.com", "claude-opus-4-8");

    [Fact]
    public async Task RunAsync_Succeeds_RecordsDraftPrAndAudit()
    {
        var store = new InMemoryFixStore();
        var result = await RunAsync(new FixOrchestrator(store, new FakeFixProvider()), Job);

        Assert.Equal(FixStatus.Succeeded, result.Status);
        Assert.Equal("fake", result.Provider);
        Assert.Equal(42, result.IssueId);
        Assert.Contains("example.invalid", result.PrUrl);
        Assert.NotEqual("", result.Branch);

        // The persisted row reflects the final state, and the run is audited end to end.
        Assert.Equal(FixStatus.Succeeded, store.Current[result.Id].Status);
        Assert.Null(store.Current[result.Id].FailureReason);
        Assert.Equal(["requested", "draft_pr_opened"], store.Events(result.Id));
    }

    [Fact]
    public async Task RunAsync_ProviderKnowsWhy_RecordsThatReason()
    {
        var store = new InMemoryFixStore();
        var provider = new ThrowingProvider(new FixFailedException(FixFailureReason.NoSourceFiles, "none"));

        var result = await RunAsync(new FixOrchestrator(store, provider), Job);

        Assert.Equal(FixFailureReason.NoSourceFiles, store.Current[result.Id].FailureReason);
    }

    [Fact]
    public async Task RunAsync_ProviderThrows_MarksFailedAndAudits()
    {
        var store = new InMemoryFixStore();
        var result = await RunAsync(new FixOrchestrator(store, new ThrowingProvider()), Job);

        Assert.Equal(FixStatus.Failed, result.Status);
        Assert.Equal(FixStatus.Failed, store.Current[result.Id].Status);
        Assert.Equal(["requested", "failed"], store.Events(result.Id));
    }

    [Fact]
    public async Task RunAsync_WithManagedAgentProvider_DrivesTheFullStartPollLifecycle()
    {
        // The managed provider orchestrates a simulated backend end to end (start → poll → draft PR),
        // so the orchestrator records a SUCCEEDED run named after the backend, with no LLM or real PR.
        var store = new InMemoryFixStore();
        var provider = new ManagedAgentFixProvider(
            new SimulatedAgentGateway(pollsWhileRunning: 1),
            new ManagedAgentOptions(TimeSpan.Zero, MaxPolls: 10),
            (_, _) => Task.CompletedTask);

        var result = await RunAsync(new FixOrchestrator(store, provider), Job);

        Assert.Equal(FixStatus.Succeeded, result.Status);
        Assert.Equal("simulated-agent", result.Provider);
        Assert.Contains("example.invalid", result.PrUrl);
        Assert.Equal(["requested", "draft_pr_opened"], store.Events(result.Id));
    }

    [Fact]
    public async Task RunAsync_AuditsWhoRepoProviderModelAndPromptHash()
    {
        var store = new InMemoryFixStore();
        var result = await RunAsync(new FixOrchestrator(store, new FakeFixProvider()), Job with { Prompt = "Assembled, scrubbed context." });

        using var requested = JsonDocument.Parse(store.Detail(result.Id, "requested"));
        Assert.Equal("acme/api", requested.RootElement.GetProperty("repo").GetString());
        Assert.Equal("claude-opus-4-8", requested.RootElement.GetProperty("model").GetString());
        Assert.Equal("fake", requested.RootElement.GetProperty("provider").GetString());
        Assert.Equal(64, requested.RootElement.GetProperty("promptHash").GetString()!.Length); // SHA-256 hex

        using var opened = JsonDocument.Parse(store.Detail(result.Id, "draft_pr_opened"));
        Assert.Contains("example.invalid", opened.RootElement.GetProperty("prUrl").GetString());
        Assert.NotEqual("", opened.RootElement.GetProperty("branch").GetString());
        // Token usage is audited per run (0 for the no-model fake) so pricing calibrates on real cost.
        Assert.Equal(0, opened.RootElement.GetProperty("inputTokens").GetInt64());
        Assert.Equal(0, opened.RootElement.GetProperty("outputTokens").GetInt64());
    }

    [Fact]
    public async Task RunAsync_PassesTheJobsAssembledPrompt_FallingBackWhenEmpty()
    {
        var capturing = new CapturingProvider();

        await new FixOrchestrator(new InMemoryFixStore(), capturing)
            .RunAsync(Job with { Prompt = "Assembled, scrubbed context." });
        Assert.Equal("Assembled, scrubbed context.", capturing.LastPrompt);

        await new FixOrchestrator(new InMemoryFixStore(), capturing).RunAsync(Job);
        Assert.Equal("Fix issue 42.", capturing.LastPrompt); // empty prompt → minimal fallback
    }

    [Fact]
    public async Task RunAsync_NamesTheRequestByTheRunsOwnId()
    {
        // The branch is built from it, and the stale-run sweep rebuilds that branch from the row's id.
        var capturing = new CapturingProvider();

        var run = await new FixOrchestrator(new InMemoryFixStore(), capturing).RunAsync(Job);

        Assert.Equal(run!.Id, capturing.LastRunId);
    }

    [Fact]
    public async Task RunAsync_NamesTheRequestByTheQueuedRunItClaims()
    {
        // The usual path: the request wrote the run, so its id, not the one the job would have minted, is
        // the row the sweep reads back.
        var store = new InMemoryFixStore();
        var capturing = new CapturingProvider();
        var job = Job with { JobId = Guid.CreateVersion7(), ReservedAt = DateTimeOffset.UtcNow };
        var queued = job.ToPendingRun(provider: "", DateTimeOffset.UtcNow);
        Assert.True(await store.TryQueueAsync(queued));

        await new FixOrchestrator(store, capturing).RunAsync(job);

        Assert.Equal(queued.Id, capturing.LastRunId);
    }

    [Fact]
    public async Task RunAsync_RefundsTheReservation_OnlyWhenTheRunFails()
    {
        // A failed run gives the reservation back; a successful one keeps it consumed.
        var failing = new InMemoryFixStore();
        await new FixOrchestrator(failing, new ThrowingProvider()).RunAsync(Job);
        Assert.Single(failing.Refunds);

        var succeeding = new InMemoryFixStore();
        await new FixOrchestrator(succeeding, new FakeFixProvider()).RunAsync(Job);
        Assert.Empty(succeeding.Refunds);
    }

    [Fact]
    public async Task RunAsync_ProviderTimesOut_MarksFailedAndRefunds()
    {
        // An HttpClient timeout surfaces as TaskCanceledException while the caller's token is untouched.
        // It is a failed run, not a shutdown: letting it escape left the run Running and the allowance
        // reserved, and stopped the worker that called it.
        var store = new InMemoryFixStore();
        var provider = new ThrowingProvider(new TaskCanceledException("timed out"));

        var result = await RunAsync(new FixOrchestrator(store, provider), Job);

        Assert.Equal(FixStatus.Failed, store.Current[result.Id].Status);
        Assert.Equal(FixFailureReason.TimedOut, store.Current[result.Id].FailureReason);
        Assert.Equal(["requested", "failed"], store.Events(result.Id));
        Assert.Single(store.Refunds);
    }

    [Fact]
    public async Task RunAsync_RecordingTheOpenedPrFails_NeitherFailsTheRunNorRefunds()
    {
        // The draft PR exists once the provider returns. An error recording it used to land in the
        // failure catch, marking the run Failed and refunding an allowance that was spent.
        var store = new InMemoryFixStore { FailAuditOf = "draft_pr_opened" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FixOrchestrator(store, new FakeFixProvider()).RunAsync(Job));

        Assert.Equal(FixStatus.Succeeded, Assert.Single(store.Current.Values).Status);
        Assert.Empty(store.Refunds);
    }

    [Fact]
    public async Task RunAsync_FailsDuringShutdown_StillRecordsTheFailureAndRefunds()
    {
        // An ordinary error that lands while the worker is stopping enters the catch with the token
        // already cancelled. Repair writes on that token threw, leaving the run Running and its
        // allowance held for ever.
        var store = new InMemoryFixStore();
        using var stopping = new CancellationTokenSource();
        var provider = new StoppingProvider(stopping, () => new InvalidOperationException("connection closed during shutdown"));

        var result = await RunAsync(new FixOrchestrator(store, provider), Job, stopping.Token);

        Assert.Equal(FixStatus.Failed, store.Current[result.Id].Status);
        Assert.Single(store.Refunds);
    }

    [Fact]
    public async Task RunAsync_AJobDeliveredAgain_DoesNotRunTwice()
    {
        // A redelivered job would otherwise open a second draft PR on the one reservation it carries.
        var store = new InMemoryFixStore();
        var provider = new CapturingProvider();
        var job = Job with { JobId = Guid.CreateVersion7(), ReservedAt = DateTimeOffset.UtcNow };

        Assert.NotNull(await new FixOrchestrator(store, provider).RunAsync(job));
        provider.Reset();
        Assert.Null(await new FixOrchestrator(store, provider).RunAsync(job));

        Assert.Null(provider.LastPrompt);
        Assert.Single(store.Current.Values);
    }

    [Fact]
    public async Task RunAsync_AJobWithNoId_IsNeverTreatedAsDeliveredAgain()
    {
        // A job queued before jobs carried an id has none to compare, so each delivery runs.
        var store = new InMemoryFixStore();

        await new FixOrchestrator(store, new FakeFixProvider()).RunAsync(Job);
        await new FixOrchestrator(store, new FakeFixProvider()).RunAsync(Job);

        Assert.Equal(2, store.Current.Count);
        Assert.All(store.Current.Values, r => Assert.Null(r.JobId));
    }

    [Fact]
    public async Task RunAsync_ClaimsThePendingRunItsRequestWrote_RatherThanAddingOne()
    {
        // A hosted request writes its run pending before it publishes the job, which is what refuses a
        // second request for the issue while this one waits. The job takes that run over.
        var store = new InMemoryFixStore();
        var job = Job with { JobId = Guid.CreateVersion7(), ReservedAt = DateTimeOffset.UtcNow };
        var queued = job.ToPendingRun(provider: "", DateTimeOffset.UtcNow);
        Assert.True(await store.TryQueueAsync(queued));

        var result = await RunAsync(new FixOrchestrator(store, new FakeFixProvider()), job);

        Assert.Equal(queued.Id, result.Id);
        var stored = Assert.Single(store.Current.Values);
        Assert.Equal(FixStatus.Succeeded, stored.Status);
        Assert.Equal("fake", stored.Provider);
        Assert.Equal(["requested", "draft_pr_opened"], store.Events(queued.Id));
    }

    [Fact]
    public async Task RunAsync_AJobWhoseQueuedRunTheSweepFailed_DoesNotRun()
    {
        // The sweep took the job for lost and refunded its run, so running it now would spend a run that
        // nobody holds.
        var store = new InMemoryFixStore();
        var provider = new CapturingProvider();
        var job = Job with { JobId = Guid.CreateVersion7(), ReservedAt = DateTimeOffset.UtcNow };
        var queued = job.ToPendingRun(provider: "", DateTimeOffset.UtcNow);
        store.Current[queued.Id] = queued with { Status = FixStatus.Failed, FailureReason = FixFailureReason.Interrupted };

        Assert.Null(await new FixOrchestrator(store, provider).RunAsync(job));

        Assert.Null(provider.LastPrompt);
        Assert.Equal(FixStatus.Failed, Assert.Single(store.Current.Values).Status);
        Assert.Empty(store.Refunds);
    }

    [Fact]
    public async Task RunAsync_ARunTheSweepRefundedThatThenSucceeds_IsChargedAgain()
    {
        // The sweep gave the run back on the belief it had died. Its PR says otherwise.
        var store = new InMemoryFixStore();
        var reservedAt = new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero);

        await new FixOrchestrator(store, new SweptThenSucceedsProvider(store))
            .RunAsync(Job with { ReservedAt = reservedAt });

        Assert.Equal([reservedAt], store.Recharges);
        Assert.Equal(FixStatus.Succeeded, Assert.Single(store.Current.Values).Status);
    }

    // Shuts the caller down, then fails with the given error, or finishes if there is none: a run cut
    // off mid-shutdown, or one whose PR opened just as the worker was told to stop.
    private sealed class StoppingProvider(CancellationTokenSource stopping, Func<Exception>? error) : IFixProvider
    {
        public string Name => "stopping";

        public async Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
        {
            await stopping.CancelAsync();
            return error is null
                ? new FixResult("condux/fix-42", "https://example.invalid/x/pull/1", "s")
                : throw error();
        }
    }

    [Fact]
    public async Task RunAsync_CallerCancels_PropagatesTheCancellation()
    {
        // The worker stopping mid-run is the one cancellation that passes through; the run is not failed.
        using var stopping = new CancellationTokenSource();
        var provider = new StoppingProvider(stopping, () => new OperationCanceledException(stopping.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FixOrchestrator(new InMemoryFixStore(), provider).RunAsync(Job, stopping.Token));
    }

    [Fact]
    public async Task RunAsync_ShutdownAfterThePrOpens_StillRecordsIt()
    {
        // The job is not delivered again, so a PR the shutdown kept out of the store is recorded nowhere.
        var store = new InMemoryFixStore();
        using var stopping = new CancellationTokenSource();
        var provider = new StoppingProvider(stopping, error: null);

        var result = await RunAsync(new FixOrchestrator(store, provider), Job, stopping.Token);

        Assert.Equal(FixStatus.Succeeded, store.Current[result.Id].Status);
    }

    [Fact]
    public async Task RunAsync_RefundsTheMonthTheRunWasReservedIn()
    {
        // The counter holds one month. A job reserved just before midnight on the last day and taken after
        // it must hand back the old month, not the new one the run was created in.
        var store = new InMemoryFixStore();
        var reservedAt = new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero);

        var run = await RunAsync(
            new FixOrchestrator(store, new ThrowingProvider()), Job with { ReservedAt = reservedAt });

        Assert.Equal(reservedAt, run.ReservedAt);
        Assert.Equal([reservedAt], store.Refunds);
    }

    [Fact]
    public async Task RunAsync_AJobWithNoReservationInstant_RefundsTheMonthItWasCreatedIn()
    {
        // A job queued before jobs carried the instant falls back to the row's own creation time.
        var store = new InMemoryFixStore();

        var run = await RunAsync(new FixOrchestrator(store, new ThrowingProvider()), Job);

        Assert.Null(run.ReservedAt);
        Assert.Equal([run.CreatedAt], store.Refunds);
    }

    [Fact]
    public async Task RunAsync_ARunTheSweepAlreadyConcluded_IsNotRefundedAgain()
    {
        // A run that outlived the stale-run cutoff was failed and refunded by the sweep. When it fails
        // for real afterwards, a second refund would hand the org a run it never paid for.
        var store = new InMemoryFixStore();
        var provider = new SweptMidRunProvider(store);

        var result = await RunAsync(new FixOrchestrator(store, provider), Job);

        Assert.Empty(store.Refunds);
        Assert.Equal(FixFailureReason.Interrupted, store.Current[result.Id].FailureReason);
        // The real error is kept for operators, under its own event so the trail does not read as two failures.
        Assert.Contains("the run failed after all", store.Detail(result.Id, "failed_late"));
        Assert.DoesNotContain("failed", store.Events(result.Id));
    }

    [Fact]
    public async Task RunAsync_AFailedAuditDoesNotCostTheRefund()
    {
        // The refund commits with the failure, so an audit that fails afterwards cannot leave it held.
        var store = new InMemoryFixStore { FailAuditOf = "failed" };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FixOrchestrator(store, new ThrowingProvider()).RunAsync(Job));

        Assert.Single(store.Refunds);
    }

    [Fact]
    public async Task RunAsync_ARunWhoseRowWasDeleted_KeepsItsReservation()
    {
        // Deleting an issue deletes its runs. A deleted row cannot say whether the sweep already refunded
        // it, so the failure path refunds nothing rather than risk refunding twice.
        var store = new InMemoryFixStore();

        // The audit then fails on its foreign key, as it does against Postgres.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FixOrchestrator(store, new DeletingProvider(store)).RunAsync(Job));

        Assert.Empty(store.Refunds);
    }

    // A run of a job with no id always runs, so its result is never null.
    private static async Task<FixSuggestion> RunAsync(
        FixOrchestrator orchestrator, FixJob job, CancellationToken cancellationToken = default) =>
        await orchestrator.RunAsync(job, cancellationToken)
        ?? throw new InvalidOperationException("the job was treated as delivered again");

    [Fact]
    public async Task RunAsync_ARunCancelledBeforeItIsMarkedRunning_IsNotRun()
    {
        // A job with no pending run to claim inserts its own, which stays pending until it is marked running,
        // and an admin may cancel it (or the sweep fail it) in between. Both refunded it, so it must not run.
        var store = new InMemoryFixStore { AfterInsert = s => s with { Status = FixStatus.Cancelled } };
        var provider = new CountingProvider();

        var result = await new FixOrchestrator(store, provider).RunAsync(Job);

        Assert.Null(result);
        Assert.Equal(0, provider.Runs);
        Assert.Equal(FixStatus.Cancelled, Assert.Single(store.Current.Values).Status);
        Assert.Empty(store.Refunds);
    }

    private sealed class CountingProvider : IFixProvider
    {
        public string Name => "counting";

        public int Runs { get; private set; }

        public Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
        {
            Runs++;
            return new FakeFixProvider().GenerateFixAsync(request, cancellationToken);
        }
    }

    private sealed class DeletingProvider(InMemoryFixStore store) : IFixProvider
    {
        public string Name => "deleting";

        public Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
        {
            store.Current.Clear();
            throw new InvalidOperationException("the row went away");
        }
    }

    // Plays the sweep: while the run is in progress, marks it failed as interrupted, then fails itself.
    private sealed class SweptMidRunProvider(InMemoryFixStore store) : IFixProvider
    {
        public string Name => "swept";

        public Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
        {
            var running = store.Current.Values.Single(s => s.Status == FixStatus.Running);
            store.Current[running.Id] = running with
            {
                Status = FixStatus.Failed,
                FailureReason = FixFailureReason.Interrupted,
            };
            throw new InvalidOperationException("the run failed after all");
        }
    }

    // Plays the sweep, then finishes: a run the sweep failed and refunded whose PR opens after all.
    private sealed class SweptThenSucceedsProvider(InMemoryFixStore store) : IFixProvider
    {
        public string Name => "swept-then-succeeds";

        public Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
        {
            var running = store.Current.Values.Single(s => s.Status == FixStatus.Running);
            store.Current[running.Id] = running with
            {
                Status = FixStatus.Failed,
                FailureReason = FixFailureReason.Interrupted,
            };
            return Task.FromResult(new FixResult("condux/fix-42", "https://example.invalid/x/pull/2", "s"));
        }
    }

    private sealed class ThrowingProvider(Exception? error = null) : IFixProvider
    {
        public string Name => "throwing";

        public Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default) =>
            throw error ?? new InvalidOperationException("provider boom");
    }

    private sealed class CapturingProvider : IFixProvider
    {
        public string? LastPrompt { get; private set; }

        public Guid? LastRunId { get; private set; }

        public string Name => "capturing";

        public void Reset() => LastPrompt = null;

        public Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
        {
            LastPrompt = request.Prompt;
            LastRunId = request.RunId;
            return Task.FromResult(new FixResult("condux/fix-42", "https://example.invalid/x/pull/0", "s"));
        }
    }

    private sealed class InMemoryFixStore : IFixStore
    {
        public readonly Dictionary<Guid, FixSuggestion> Current = [];
        private readonly List<(Guid FixId, string Event, string Detail)> _audit = [];

        public string[] Events(Guid fixId) => [.. _audit.Where(a => a.FixId == fixId).Select(a => a.Event)];

        public string Detail(Guid fixId, string eventName) =>
            _audit.First(a => a.FixId == fixId && a.Event == eventName).Detail;

        /// <summary>The month each refund went to, as the instant it was computed from.</summary>
        public List<DateTimeOffset> Refunds { get; } = [];

        /// <summary>The month each re-charge went to, as the instant it was computed from.</summary>
        public List<DateTimeOffset> Recharges { get; } = [];

        // Like the two unique indexes: a job id seen before (never a NULL one), or a run in progress on the
        // issue.
        public Task<bool> InsertAsync(FixSuggestion s, CancellationToken ct = default)
        {
            if ((s.JobId is { } jobId && Current.Values.Any(c => c.JobId == jobId))
                || Current.Values.Any(c => c.IssueId == s.IssueId && c.Status is FixStatus.Pending or FixStatus.Running))
            {
                return Task.FromResult(false);
            }
            Current[s.Id] = AfterInsert?.Invoke(s) ?? s;
            return Task.FromResult(true);
        }

        /// <summary>What the row is by the time the orchestrator next writes it, standing in for a write that
        /// lands between the insert and marking the run running.</summary>
        public Func<FixSuggestion, FixSuggestion>? AfterInsert { get; init; }

        // Like the SQL: refused while the issue has a run in progress, or the job has a run already.
        public Task<bool> TryQueueAsync(FixSuggestion s, CancellationToken ct = default)
        {
            if (Current.Values.Any(c => (c.JobId is { } jobId && jobId == s.JobId)
                || (c.IssueId == s.IssueId && c.Status is FixStatus.Pending or FixStatus.Running)))
            {
                return Task.FromResult(false);
            }
            Current[s.Id] = s;
            return Task.FromResult(true);
        }

        // Like the SQL: an upsert on the id, so a run never written is written already cancelled.
        public Task<bool> TryCancelQueuedAsync(FixSuggestion s, CancellationToken ct = default)
        {
            if (!Current.TryGetValue(s.Id, out var stored))
            {
                Current[s.Id] = s with { Status = FixStatus.Cancelled };
                return Task.FromResult(true);
            }
            if (stored.Status != FixStatus.Pending)
            {
                return Task.FromResult(false);
            }
            Current[s.Id] = stored with { Status = FixStatus.Cancelled };
            return Task.FromResult(true);
        }

        // Like the SQL: only the job's pending run is claimed.
        public Task<Guid?> TryClaimAsync(FixSuggestion s, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (s.JobId is not { } jobId
                || Current.Values.FirstOrDefault(c => c.JobId == jobId && c.Status == FixStatus.Pending) is not { } stored)
            {
                return Task.FromResult<Guid?>(null);
            }
            Current[stored.Id] = stored with
            {
                Status = FixStatus.Running,
                Provider = s.Provider,
                Model = s.Model,
                UpdatedAt = s.UpdatedAt,
            };
            return Task.FromResult<Guid?>(stored.Id);
        }

        public Task RecordSuccessAsync(FixSuggestion s, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!Current.TryGetValue(s.Id, out var stored))
            {
                return Task.CompletedTask;
            }
            if (stored is { Status: FixStatus.Failed, FailureReason: FixFailureReason.Interrupted })
            {
                Recharges.Add(stored.ReservedAt ?? stored.CreatedAt);
            }
            Current[s.Id] = s;
            return Task.CompletedTask;
        }

        // Like the SQL: only over a pending run.
        public Task<bool> MarkRunningAsync(FixSuggestion s, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Current.TryGetValue(s.Id, out var stored) && stored.Status == FixStatus.Pending)
            {
                Current[s.Id] = s;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<bool> TryFailAsync(FixSuggestion s, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            // Like the SQL: a missing row is not written back, and matches nothing.
            if (!Current.TryGetValue(s.Id, out var stored)
                || stored.Status is not (FixStatus.Pending or FixStatus.Running))
            {
                return Task.FromResult(false);
            }
            Current[s.Id] = s;
            Refunds.Add(stored.ReservedAt ?? stored.CreatedAt);
            return Task.FromResult(true);
        }

        /// <summary>An audit event whose write fails, as a database timeout would.</summary>
        public string? FailAuditOf { get; init; }

        public Task AppendAuditAsync(Guid fixId, string actor, string eventName, string detailJson, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!Current.ContainsKey(fixId))
            {
                throw new InvalidOperationException("audit row violates its foreign key to the run");
            }
            if (eventName == FailAuditOf)
            {
                throw new InvalidOperationException("audit write failed");
            }

            _audit.Add((fixId, eventName, detailJson));
            return Task.CompletedTask;
        }

        public Task<FixSuggestion?> GetAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(Current.TryGetValue(id, out var s) ? s : null);

        public Task<IReadOnlyList<FixSuggestion>> ListByIssueAsync(long issueId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FixSuggestion>>([.. Current.Values.Where(s => s.IssueId == issueId)]);
    }
}
