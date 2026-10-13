using Condux.Core.CveFix;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The pending run a hosted request writes before it publishes its job, against real Postgres: one run in
/// progress per issue or advisory, the conductor claiming the run its job names, and a run the job never
/// reached being cancelled. The orchestrators' use of these is unit-tested; this pins the SQL.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FixRunQueueStoreTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task An_issue_holds_one_run_in_progress_and_frees_it_when_the_run_concludes()
    {
        var issueId = await SeedIssueAsync();
        var first = Job(issueId).ToPendingRun(provider: "", Now);

        Assert.True(await Fixes().TryQueueAsync(first));
        Assert.False(await Fixes().TryQueueAsync(Job(issueId).ToPendingRun(provider: "", Now)));

        await Fixes().UpdateAsync(first with { Status = FixStatus.Failed });
        Assert.True(await Fixes().TryQueueAsync(Job(issueId).ToPendingRun(provider: "", Now)));
    }

    [Fact]
    public async Task The_conductor_claims_the_run_its_job_names_once_and_with_its_provider()
    {
        var job = Job(await SeedIssueAsync());
        var queued = job.ToPendingRun(provider: "", Now);
        Assert.True(await Fixes().TryQueueAsync(queued));

        var claimed = await Fixes().TryClaimAsync(job.ToPendingRun("anthropic", Now.AddMinutes(1)));

        Assert.Equal(queued.Id, claimed);
        var run = (await Fixes().GetAsync(queued.Id))!;
        Assert.Equal(FixStatus.Running, run.Status);
        Assert.Equal("anthropic", run.Provider);
        Assert.Equal(Now.AddMinutes(1), run.UpdatedAt);
        // A second delivery of the job finds the run no longer pending.
        Assert.Null(await Fixes().TryClaimAsync(job.ToPendingRun("anthropic", Now)));
    }

    [Fact]
    public async Task A_run_the_sweep_failed_cannot_be_claimed()
    {
        var job = Job(await SeedIssueAsync());
        var queued = job.ToPendingRun(provider: "", Now);
        await Fixes().TryQueueAsync(queued);
        await Fixes().UpdateAsync(queued with { Status = FixStatus.Failed, FailureReason = FixFailureReason.Interrupted });

        Assert.Null(await Fixes().TryClaimAsync(job.ToPendingRun("anthropic", Now)));
        // Nor inserted again as if the job had none, since the run it names already exists.
        Assert.False(await Fixes().InsertAsync(job.ToPendingRun("anthropic", Now)));
    }

    [Fact]
    public async Task Only_a_pending_run_is_cancelled_and_its_job_is_then_refused()
    {
        // The row and its job id stay, so a job that reached the broker although its publish failed meets
        // its own run and neither claims it nor inserts another.
        var issueId = await SeedIssueAsync();
        var job = Job(issueId);
        var pending = job.ToPendingRun(provider: "", Now);
        await Fixes().TryQueueAsync(pending);

        Assert.True(await Fixes().TryCancelQueuedAsync(pending));
        Assert.Equal(FixStatus.Cancelled, (await Fixes().GetAsync(pending.Id))!.Status);
        Assert.Null(await Fixes().TryClaimAsync(job.ToPendingRun("anthropic", Now)));
        Assert.False(await Fixes().InsertAsync(job.ToPendingRun("anthropic", Now)));

        var claimedJob = Job(issueId);
        var claimed = claimedJob.ToPendingRun(provider: "", Now);
        await Fixes().TryQueueAsync(claimed);
        await Fixes().TryClaimAsync(claimedJob.ToPendingRun("anthropic", Now));
        Assert.False(await Fixes().TryCancelQueuedAsync(claimed));
        Assert.Equal(FixStatus.Running, (await Fixes().GetAsync(claimed.Id))!.Status);
    }

    [Fact]
    public async Task A_cancel_that_lands_before_its_write_is_not_overtaken_by_it()
    {
        // The write may still be in flight after its client gave up. The cancel writes the run already
        // cancelled, so the late write meets it and writes nothing, and no pending run appears for a
        // reservation that was handed back.
        var issueId = await SeedIssueAsync();
        var job = Job(issueId);
        var run = job.ToPendingRun(provider: "", Now);

        Assert.True(await Fixes().TryCancelQueuedAsync(run));
        Assert.False(await Fixes().TryQueueAsync(run));
        Assert.Equal(FixStatus.Cancelled, (await Fixes().GetAsync(run.Id))!.Status);

        // A runner's insert, which is the other write this settles, meets it the same way.
        var runnerRow = Job(issueId).ToPendingRun("runner", Now);
        Assert.True(await Fixes().TryCancelQueuedAsync(runnerRow));
        Assert.False(await new PostgresJobLeaseStore(pg.ConnectionString).EnqueueAsync(
            runnerRow.Id, issueId, "acme/api", new RunnerJobContext("main", "Fix it.", []), Now));
        // And the issue is free for the next request.
        Assert.True(await Fixes().TryQueueAsync(Job(issueId).ToPendingRun(provider: "", Now)));
    }

    [Fact]
    public async Task A_cancel_waits_for_a_write_still_in_flight_and_then_cancels_it()
    {
        // The case the upsert exists for: the write's client gave up while the server was still inside the
        // insert. An update would not see the uncommitted row and would answer as if nothing was written.
        var issueId = await SeedIssueAsync();
        var run = Job(issueId).ToPendingRun(provider: "", Now);
        await using var writer = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await writer.OpenAsync();
        await using var inFlight = await writer.BeginTransactionAsync();
        await using (var insert = new Npgsql.NpgsqlCommand(
            """
            INSERT INTO fix_suggestions (id, issue_id, repo_full_name, status, provider, model, branch, pr_url,
                summary, created_at, updated_at, job_id, reserved_at)
            VALUES (@id, @issue, 'acme/api', 1, '', 'model', '', '', '', now(), now(), @job, now());
            """, writer, inFlight))
        {
            insert.Parameters.AddWithValue("id", run.Id);
            insert.Parameters.AddWithValue("issue", issueId);
            insert.Parameters.AddWithValue("job", run.JobId!.Value);
            await insert.ExecuteNonQueryAsync();
        }

        var cancel = Fixes().TryCancelQueuedAsync(run);
        await WaitUntilBlockedOnALockAsync();
        Assert.False(cancel.IsCompleted);
        await inFlight.CommitAsync();

        Assert.True(await cancel.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(FixStatus.Cancelled, (await Fixes().GetAsync(run.Id))!.Status);
    }

    // Polls the server until some session of this database waits on a lock, which is the cancel waiting
    // on the uncommitted insert. Bounded, so a cancel that never waits fails the test instead of hanging it.
    private async Task WaitUntilBlockedOnALockAsync()
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var cmd = new Npgsql.NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'",
                conn);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0)
            {
                return;
            }
            await Task.Yield();
        }
        throw new TimeoutException("the cancel never waited on the in-flight write");
    }

    [Fact]
    public async Task A_bump_cancel_that_lands_before_its_write_is_not_overtaken_by_it()
    {
        var repoId = await SeedRepoAsync();
        var cves = new PostgresCveFixStore(pg.ConnectionString);
        var run = Bump(repoId).ToPendingRun(provider: "", Now);

        Assert.True(await cves.TryCancelQueuedAsync(run));
        Assert.False(await cves.TryQueueAsync(run));

        var runnerRow = Bump(repoId).ToPendingRun("runner", Now);
        Assert.True(await cves.TryCancelQueuedAsync(runnerRow));
        Assert.False(await new PostgresJobLeaseStore(pg.ConnectionString).EnqueueCveAsync(
            runnerRow, new RunnerJobContext("main", "Bump it.", ["package.json"])));
        Assert.All(await cves.ListByRepoAsync(repoId), r => Assert.Equal(FixStatus.Cancelled, r.Status));
    }

    [Fact]
    public async Task A_job_with_no_run_to_claim_is_not_inserted_beside_a_run_in_progress()
    {
        // The fallback insert names no conflict target, so the in-progress index refuses it rather than
        // raising a unique violation before the orchestrator can tell.
        var issueId = await SeedIssueAsync();
        Assert.True(await Fixes().TryQueueAsync(Job(issueId).ToPendingRun(provider: "", Now)));

        Assert.False(await Fixes().InsertAsync(Job(issueId).ToPendingRun("anthropic", Now)));
    }

    [Fact]
    public async Task An_advisory_holds_one_run_in_progress_and_the_conductor_claims_it()
    {
        var repoId = await SeedRepoAsync();
        var job = Bump(repoId);
        var queued = job.ToPendingRun(provider: "", Now);
        var cves = new PostgresCveFixStore(pg.ConnectionString);

        Assert.True(await cves.TryQueueAsync(queued));
        Assert.False(await cves.TryQueueAsync(Bump(repoId).ToPendingRun(provider: "", Now)));
        Assert.Equal(queued.Id, await cves.TryClaimAsync(job.ToPendingRun("anthropic", Now)));
        Assert.Null(await cves.TryClaimAsync(job.ToPendingRun("anthropic", Now)));

        var run = Assert.Single(await cves.ListByRepoAsync(repoId));
        Assert.Equal(FixStatus.Running, run.Status);
        Assert.Equal("anthropic", run.Provider);
    }

    [Fact]
    public async Task Only_a_pending_bump_is_cancelled_and_its_job_is_then_refused()
    {
        var repoId = await SeedRepoAsync();
        var cves = new PostgresCveFixStore(pg.ConnectionString);
        var job = Bump(repoId);
        var pending = job.ToPendingRun(provider: "", Now);
        await cves.TryQueueAsync(pending);

        Assert.True(await cves.TryCancelQueuedAsync(pending));
        Assert.Null(await cves.TryClaimAsync(job.ToPendingRun("anthropic", Now)));
        // Its own run exists, so the fallback insert refuses it without a second refund.
        Assert.False(await cves.InsertAsync(job.ToPendingRun("anthropic", Now)));
        Assert.Equal(FixStatus.Cancelled, Assert.Single(await cves.ListByRepoAsync(repoId)).Status);

        var claimedJob = Bump(repoId);
        var claimed = claimedJob.ToPendingRun(provider: "", Now);
        await cves.TryQueueAsync(claimed);
        await cves.TryClaimAsync(claimedJob.ToPendingRun("anthropic", Now));
        Assert.False(await cves.TryCancelQueuedAsync(claimed));
    }

    private PostgresFixStore Fixes() => new(pg.ConnectionString);

    private static FixJob Job(long issueId) =>
        new(issueId, "acme/api", "main", "42", "model") { JobId = Guid.CreateVersion7(), ReservedAt = Now };

    private static CveFixJob Bump(Guid repoId) =>
        new(repoId, "acme/api", "main", "GHSA-jf85-cpcp-j695", "CVE-2021-23337", "lodash", "npm", "< 4.17.21",
            "4.17.21", "https://example.invalid/advisory", "42", "model")
        { JobId = Guid.CreateVersion7(), ReservedAt = Now };

    private async Task<long> SeedIssueAsync()
    {
        var (_, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping($"fp-{Guid.NewGuid():N}", "TypeError: boom", "run"), Level.Error, Now);
        return issue.Id;
    }

    private async Task<Guid> SeedRepoAsync()
    {
        var (_, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        return (await new RepoLinkRepository(pg.ConnectionString).LinkAsync(projectId, "acme/api", "main")).Id;
    }
}
