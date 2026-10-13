using Condux.Core.CveFix;
using Condux.Core.FixEngine;
using Xunit;

namespace Condux.Core.Tests;

public class CveFixOrchestratorTests
{
    private static readonly CveFixJob Job = new(
        Guid.NewGuid(), "acme/api", "main", "GHSA-jf85-cpcp-j695", "CVE-2019-10744",
        "lodash", "npm", "< 4.17.12", "4.17.12", "https://gh/acme/api/security/dependabot/1",
        "dev@example.com", "claude-opus-4-8")
    {
        Prompt = "Bump lodash to 4.17.12.",
        ScopedPaths = ["package.json"],
    };

    [Fact]
    public async Task RunAsync_Succeeds_RecordsTheDraftBumpPr()
    {
        var store = new InMemoryCveFixStore();
        var run = (await RunAsync(new CveFixOrchestrator(store, new FakeFixProvider()), Job)).Run;

        Assert.Equal(FixStatus.Succeeded, run.Status);
        Assert.Equal("fake", run.Provider);
        Assert.Equal("GHSA-jf85-cpcp-j695", run.GhsaId);
        Assert.Equal("lodash", run.Package);
        Assert.Equal("4.17.12", run.ToVersion);
        Assert.Contains("example.invalid", run.PrUrl);
        Assert.NotEqual("", run.Branch);

        // The persisted row reflects the final state.
        Assert.Equal(FixStatus.Succeeded, store.Current[run.Id].Status);
    }

    [Fact]
    public async Task RunAsync_PassesTheAdvisoryAsTheProvidersIssueId_AndTheBumpPrompt()
    {
        var capturing = new CapturingProvider();
        var outcome = await new CveFixOrchestrator(new InMemoryCveFixStore(), capturing).RunAsync(Job);

        Assert.Equal("GHSA-jf85-cpcp-j695", capturing.LastRequest!.IssueId); // advisory rides as the issue id
        // The run's own id names the branch, so the stale-run sweep can rebuild it from the row.
        Assert.Equal(outcome!.Run.Id, capturing.LastRequest.RunId);
        Assert.Equal("Bump lodash to 4.17.12.", capturing.LastRequest.Prompt);
        Assert.Equal(["package.json"], capturing.LastRequest.ScopedPaths);
    }

    [Fact]
    public async Task RunAsync_ProviderThrows_MarksFailed()
    {
        var store = new InMemoryCveFixStore();
        var run = (await RunAsync(new CveFixOrchestrator(store, new ThrowingProvider()), Job)).Run;

        Assert.Equal(FixStatus.Failed, run.Status);
        Assert.Equal(FixStatus.Failed, store.Current[run.Id].Status);
    }

    [Fact]
    public async Task RunAsync_ReturnsTheErrorThatFailedTheRun_AndNoneForASuccess()
    {
        // A bump writes no audit, so the error handed back here is the only thing the worker can log.
        var boom = new InvalidOperationException("provider boom");

        var failed = await RunAsync(new CveFixOrchestrator(new InMemoryCveFixStore(), new ThrowingProvider(boom)), Job);
        var succeeded = await RunAsync(new CveFixOrchestrator(new InMemoryCveFixStore(), new FakeFixProvider()), Job);

        Assert.Same(boom, failed.Error);
        Assert.Null(succeeded.Error);
    }

    [Fact]
    public async Task RunAsync_ALateFailureStillReturnsItsError()
    {
        // The sweep concluded the bump first, so nothing else records why it failed in the end.
        var store = new InMemoryCveFixStore();

        var outcome = await RunAsync(new CveFixOrchestrator(store, new SweptMidRunProvider(store)), Job);

        Assert.Equal("the bump failed after all", outcome.Error?.Message);
    }

    [Fact]
    public async Task RunAsync_RefundsTheAllowance_OnlyWhenTheRunFails()
    {
        var failing = new InMemoryCveFixStore();
        await new CveFixOrchestrator(failing, new ThrowingProvider()).RunAsync(Job);
        Assert.Single(failing.Refunds);

        var succeeding = new InMemoryCveFixStore();
        await new CveFixOrchestrator(succeeding, new FakeFixProvider()).RunAsync(Job);
        Assert.Empty(succeeding.Refunds); // a success keeps the reservation
    }

    [Fact]
    public async Task RunAsync_ProviderTimesOut_MarksFailedAndRefunds()
    {
        // A timeout is a TaskCanceledException the caller never asked for: a failed run, not a shutdown.
        var store = new InMemoryCveFixStore();
        var provider = new ThrowingProvider(new TaskCanceledException("timed out"));

        var run = (await RunAsync(new CveFixOrchestrator(store, provider), Job)).Run;

        Assert.Equal(FixStatus.Failed, store.Current[run.Id].Status);
        Assert.Equal(FixFailureReason.TimedOut, store.Current[run.Id].FailureReason);
        Assert.Single(store.Refunds);
    }

    [Fact]
    public async Task RunAsync_RecordingTheOpenedPrFails_NeitherFailsTheRunNorRefunds()
    {
        // The draft PR exists once the provider returns, so an error recording it is not a failed run.
        var store = new InMemoryCveFixStore { FailSuccessWrite = true };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new CveFixOrchestrator(store, new FakeFixProvider()).RunAsync(Job));

        Assert.NotEqual(FixStatus.Failed, Assert.Single(store.Current.Values).Status);
        Assert.Empty(store.Refunds);
    }

    [Fact]
    public async Task RunAsync_FailsDuringShutdown_StillRecordsTheFailureAndRefunds()
    {
        // An ordinary error that lands mid-shutdown enters the catch with the token already cancelled.
        var store = new InMemoryCveFixStore();
        using var stopping = new CancellationTokenSource();
        var provider = new StoppingProvider(stopping, () => new InvalidOperationException("connection closed during shutdown"));

        var run = (await RunAsync(new CveFixOrchestrator(store, provider), Job, stopping.Token)).Run;

        Assert.Equal(FixStatus.Failed, store.Current[run.Id].Status);
        Assert.Single(store.Refunds);
    }

    [Fact]
    public async Task RunAsync_AJobDeliveredAgain_DoesNotRunTwice()
    {
        var store = new InMemoryCveFixStore();
        var job = Job with { JobId = Guid.CreateVersion7(), ReservedAt = DateTimeOffset.UtcNow };

        Assert.NotNull(await new CveFixOrchestrator(store, new FakeFixProvider()).RunAsync(job));
        Assert.Null(await new CveFixOrchestrator(store, new ThrowingProvider()).RunAsync(job));

        Assert.Equal(FixStatus.Succeeded, Assert.Single(store.Current.Values).Status);
    }

    [Fact]
    public async Task RunAsync_NamesTheRequestByTheQueuedRunItClaims()
    {
        var store = new InMemoryCveFixStore();
        var capturing = new CapturingProvider();
        var job = Job with { JobId = Guid.CreateVersion7(), ReservedAt = DateTimeOffset.UtcNow };
        var queued = job.ToPendingRun(provider: "", DateTimeOffset.UtcNow);
        Assert.True(await store.TryQueueAsync(queued));

        await new CveFixOrchestrator(store, capturing).RunAsync(job);

        Assert.Equal(queued.Id, capturing.LastRequest!.RunId);
    }

    [Fact]
    public async Task RunAsync_ClaimsThePendingRunItsRequestWrote_RatherThanAddingOne()
    {
        // As on the issue path: the request's pending run is what refuses a second request for the
        // advisory while this one waits, and the job takes it over.
        var store = new InMemoryCveFixStore();
        var job = Job with { JobId = Guid.CreateVersion7(), ReservedAt = DateTimeOffset.UtcNow };
        var queued = job.ToPendingRun(provider: "", DateTimeOffset.UtcNow);
        Assert.True(await store.TryQueueAsync(queued));

        var run = (await RunAsync(new CveFixOrchestrator(store, new FakeFixProvider()), job)).Run;

        Assert.Equal(queued.Id, run.Id);
        var stored = Assert.Single(store.Current.Values);
        Assert.Equal(FixStatus.Succeeded, stored.Status);
        Assert.Equal("fake", stored.Provider);
    }

    [Fact]
    public async Task RunAsync_ABumpWhoseQueuedRunTheSweepFailed_DoesNotRun()
    {
        var store = new InMemoryCveFixStore();
        var provider = new CapturingProvider();
        var job = Job with { JobId = Guid.CreateVersion7(), ReservedAt = DateTimeOffset.UtcNow };
        var queued = job.ToPendingRun(provider: "", DateTimeOffset.UtcNow);
        store.Current[queued.Id] = queued with { Status = FixStatus.Failed, FailureReason = FixFailureReason.Interrupted };

        Assert.Null(await new CveFixOrchestrator(store, provider).RunAsync(job));

        Assert.Null(provider.LastRequest);
        Assert.Equal(FixStatus.Failed, Assert.Single(store.Current.Values).Status);
        Assert.Empty(store.Refunds);
    }

    [Fact]
    public async Task RunAsync_ABumpQueuedBeforeJobsCarriedAnId_RunsOnEveryDelivery()
    {
        // Its empty id must be stored as none, or every such job after the first reads as a repeat.
        var store = new InMemoryCveFixStore();

        await new CveFixOrchestrator(store, new FakeFixProvider()).RunAsync(Job);
        await new CveFixOrchestrator(store, new FakeFixProvider()).RunAsync(Job);

        Assert.Equal(2, store.Current.Count);
        Assert.All(store.Current.Values, r => Assert.Null(r.JobId));
    }

    [Fact]
    public async Task RunAsync_ABumpWithNoReservationInstant_RefundsTheMonthItWasCreatedIn()
    {
        // A default instant would date the refund to year one, which matches no month's counter.
        var store = new InMemoryCveFixStore();

        var run = (await RunAsync(new CveFixOrchestrator(store, new ThrowingProvider()), Job)).Run;

        Assert.Null(run.ReservedAt);
        Assert.Equal([run.CreatedAt], store.Refunds);
    }

    [Fact]
    public async Task RunAsync_ABumpTheSweepRefundedThatThenSucceeds_IsChargedAgain()
    {
        var store = new InMemoryCveFixStore();
        var reservedAt = new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero);

        await new CveFixOrchestrator(store, new SweptMidRunProvider(store, fail: false))
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
            new CveFixOrchestrator(new InMemoryCveFixStore(), provider).RunAsync(Job, stopping.Token));
    }

    [Fact]
    public async Task RunAsync_ShutdownAfterThePrOpens_StillRecordsIt()
    {
        // The job is not delivered again, so a PR the shutdown kept out of the store is recorded nowhere.
        var store = new InMemoryCveFixStore();
        using var stopping = new CancellationTokenSource();
        var provider = new StoppingProvider(stopping, error: null);

        var result = (await RunAsync(new CveFixOrchestrator(store, provider), Job, stopping.Token)).Run;

        Assert.Equal(FixStatus.Succeeded, store.Current[result.Id].Status);
    }

    [Fact]
    public async Task RunAsync_RefundsTheMonthTheBumpWasReservedIn()
    {
        // The counter holds one month, and the bump was reserved before the worker took it.
        var store = new InMemoryCveFixStore();
        var reservedAt = new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero);

        var run = (await RunAsync(
            new CveFixOrchestrator(store, new ThrowingProvider()), Job with { ReservedAt = reservedAt })).Run;

        Assert.Equal(reservedAt, run.ReservedAt);
        Assert.Equal([reservedAt], store.Refunds);
    }

    [Fact]
    public async Task RunAsync_ABumpTheSweepAlreadyConcluded_IsNotRefundedAgain()
    {
        var store = new InMemoryCveFixStore();

        var run = (await RunAsync(new CveFixOrchestrator(store, new SweptMidRunProvider(store, fail: true)), Job)).Run;

        Assert.Empty(store.Refunds);
        Assert.Equal(FixFailureReason.Interrupted, store.Current[run.Id].FailureReason);
    }

    [Fact]
    public async Task RunAsync_ABumpWhoseRowWasDeleted_KeepsItsReservation()
    {
        // Unlinking the repository deletes its bumps. A deleted row cannot say whether the sweep already
        // refunded it, so the failure path refunds nothing rather than risk refunding twice.
        var store = new InMemoryCveFixStore();

        await new CveFixOrchestrator(store, new DeletingProvider(store)).RunAsync(Job);

        Assert.Empty(store.Refunds);
    }

    // A job with no id always runs, so its outcome is never null.
    private static async Task<CveFixOutcome> RunAsync(
        CveFixOrchestrator orchestrator, CveFixJob job, CancellationToken cancellationToken = default) =>
        await orchestrator.RunAsync(job, cancellationToken)
        ?? throw new InvalidOperationException("the job was treated as delivered again");

    private sealed class DeletingProvider(InMemoryCveFixStore store) : IFixProvider
    {
        public string Name => "deleting";

        public Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
        {
            store.Current.Clear();
            throw new InvalidOperationException("the row went away");
        }
    }

    // Plays the sweep: while the bump is in progress, marks it failed as interrupted, then fails itself or,
    // when told not to, opens its PR after all.
    private sealed class SweptMidRunProvider(InMemoryCveFixStore store, bool fail = true) : IFixProvider
    {
        public string Name => "swept";

        public Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
        {
            var running = store.Current.Values.Single(r => r.Status == FixStatus.Running);
            store.Current[running.Id] = running with
            {
                Status = FixStatus.Failed,
                FailureReason = FixFailureReason.Interrupted,
            };
            return fail
                ? throw new InvalidOperationException("the bump failed after all")
                : Task.FromResult(new FixResult("condux/fix-x", "https://example.invalid/x/pull/2", "s"));
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
        public FixRequest? LastRequest { get; private set; }

        public string Name => "capturing";

        public Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new FixResult("condux/fix-x", "https://example.invalid/x/pull/0", "s"));
        }
    }

    [Fact]
    public async Task RunAsync_ABumpCancelledBeforeItIsMarkedRunning_IsNotRun()
    {
        var store = new InMemoryCveFixStore { AfterInsert = r => r with { Status = FixStatus.Cancelled } };
        var provider = new CountingCveProvider();

        var outcome = await new CveFixOrchestrator(store, provider).RunAsync(Job);

        Assert.Null(outcome);
        Assert.Equal(0, provider.Runs);
        Assert.Equal(FixStatus.Cancelled, Assert.Single(store.Current.Values).Status);
        Assert.Empty(store.Refunds);
    }

    private sealed class CountingCveProvider : IFixProvider
    {
        public string Name => "counting";

        public int Runs { get; private set; }

        public Task<FixResult> GenerateFixAsync(FixRequest request, CancellationToken cancellationToken = default)
        {
            Runs++;
            return new FakeFixProvider().GenerateFixAsync(request, cancellationToken);
        }
    }

    private sealed class InMemoryCveFixStore : ICveFixStore
    {
        public readonly Dictionary<Guid, CveFixRun> Current = [];

        /// <summary>The month each refund went to, as the instant it was computed from.</summary>
        public List<DateTimeOffset> Refunds { get; } = [];

        /// <summary>The month each re-charge went to, as the instant it was computed from.</summary>
        public List<DateTimeOffset> Recharges { get; } = [];

        // Like the unique index, which never treats a NULL as a duplicate: a job with no id never collides.
        public Task<bool> InsertAsync(CveFixRun r, CancellationToken ct = default)
        {
            if (r.JobId is { } jobId && Current.Values.Any(c => c.JobId == jobId))
            {
                return Task.FromResult(false);
            }
            Current[r.Id] = AfterInsert?.Invoke(r) ?? r;
            return Task.FromResult(true);
        }

        /// <summary>What the row is by the time the orchestrator next writes it, standing in for a write that
        /// lands between the insert and marking the run running.</summary>
        public Func<CveFixRun, CveFixRun>? AfterInsert { get; init; }

        // Like the SQL: refused while the advisory has a run in progress, or the job has a run already.
        public Task<bool> TryQueueAsync(CveFixRun r, CancellationToken ct = default)
        {
            if (Current.Values.Any(c => (c.JobId is { } jobId && jobId == r.JobId)
                || (c.RepoLinkId == r.RepoLinkId && c.GhsaId == r.GhsaId
                    && c.Status is FixStatus.Pending or FixStatus.Running)))
            {
                return Task.FromResult(false);
            }
            Current[r.Id] = r;
            return Task.FromResult(true);
        }

        // Like the SQL: an upsert on the id, so a run never written is written already cancelled.
        public Task<bool> TryCancelQueuedAsync(CveFixRun r, CancellationToken ct = default)
        {
            if (!Current.TryGetValue(r.Id, out var stored))
            {
                Current[r.Id] = r with { Status = FixStatus.Cancelled };
                return Task.FromResult(true);
            }
            if (stored.Status != FixStatus.Pending)
            {
                return Task.FromResult(false);
            }
            Current[r.Id] = stored with { Status = FixStatus.Cancelled };
            return Task.FromResult(true);
        }

        // Like the SQL: only the job's pending run is claimed.
        public Task<Guid?> TryClaimAsync(CveFixRun r, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (r.JobId is not { } jobId
                || Current.Values.FirstOrDefault(c => c.JobId == jobId && c.Status == FixStatus.Pending) is not { } stored)
            {
                return Task.FromResult<Guid?>(null);
            }
            Current[stored.Id] = stored with
            {
                Status = FixStatus.Running,
                Provider = r.Provider,
                Model = r.Model,
                UpdatedAt = r.UpdatedAt,
            };
            return Task.FromResult<Guid?>(stored.Id);
        }

        public Task RecordSuccessAsync(CveFixRun r, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (FailSuccessWrite)
            {
                throw new InvalidOperationException("update failed");
            }
            if (!Current.TryGetValue(r.Id, out var stored))
            {
                return Task.CompletedTask;
            }
            if (stored is { Status: FixStatus.Failed, FailureReason: FixFailureReason.Interrupted })
            {
                Recharges.Add(stored.ReservedAt ?? stored.CreatedAt);
            }
            Current[r.Id] = r;
            return Task.CompletedTask;
        }

        /// <summary>Makes the success write fail, as a database timeout would.</summary>
        public bool FailSuccessWrite { get; init; }

        // Like the SQL: only over a pending run.
        public Task<bool> MarkRunningAsync(CveFixRun r, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (Current.TryGetValue(r.Id, out var stored) && stored.Status == FixStatus.Pending)
            {
                Current[r.Id] = r;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task<bool> TryFailAsync(CveFixRun r, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            // Like the SQL: a missing row is not written back, and matches nothing.
            if (!Current.TryGetValue(r.Id, out var stored)
                || stored.Status is not (FixStatus.Pending or FixStatus.Running))
            {
                return Task.FromResult(false);
            }
            Current[r.Id] = r;
            Refunds.Add(stored.ReservedAt ?? stored.CreatedAt);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<CveFixRun>> ListByRepoAsync(Guid repoLinkId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<CveFixRun>>([.. Current.Values.Where(r => r.RepoLinkId == repoLinkId)]);

        public Task<bool> HasActiveRunAsync(Guid repoLinkId, string ghsaId, CancellationToken ct = default) =>
            Task.FromResult(Current.Values.Any(r =>
                r.RepoLinkId == repoLinkId && r.GhsaId == ghsaId
                && r.Status is FixStatus.Pending or FixStatus.Running));
    }
}
