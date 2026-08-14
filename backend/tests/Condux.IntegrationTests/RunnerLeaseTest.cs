using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The lease against real Postgres, because everything that matters about it is concurrency and none of
/// it is reachable from a unit test: whether two runners can hold one job, whether a dead runner's work
/// comes back, and whether a finished run can be handed out again.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunnerLeaseTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private PostgresJobLeaseStore Leases() => new(pg.ConnectionString);

    private static readonly RunnerJobContext Context =
        new("main", "Fix the ValueError in checkout.", ["src/checkout.py"]);

    /// <summary>An org with one issue, ready for either kind of run to be attached to it.</summary>
    private async Task<(long OrgId, long ProjectId, long IssueId)> SeedIssueAsync()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping($"fp-{Guid.NewGuid():N}", "ValueError: boom", "run"),
            Level.Error, DateTimeOffset.UtcNow);
        return (orgId, projectId, issue.Id);
    }

    /// <summary>A run routed to a runner, which is what a claim is meant to find.</summary>
    private async Task<(long OrgId, Guid FixId)> SeedRunnerJobAsync()
    {
        var (orgId, _, issueId) = await SeedIssueAsync();
        var fixId = Guid.CreateVersion7();
        await Leases().EnqueueAsync(fixId, issueId, "acme/api", Context, DateTimeOffset.UtcNow);
        return (orgId, fixId);
    }

    [Fact]
    public async Task A_run_the_hosted_conductor_is_executing_is_never_claimable()
    {
        // The failure this exists for: the hosted orchestrator creates its run, flips it to Running and
        // never takes a lease, so for the whole length of a fix the row looks free. A runner polling the
        // same org would take work already under way and open a second draft pull request for one issue.
        var (orgId, _, issueId) = await SeedIssueAsync();
        var hosted = new FixSuggestion(
            Guid.CreateVersion7(), issueId, "acme/api", FixStatus.Running,
            "anthropic", ModelDefaults.Fix, "", "", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await new PostgresFixStore(pg.ConnectionString).InsertAsync(hosted);

        Assert.Null(await Leases().TryClaimAsync(orgId, Now));

        // And the exclusion is about who the work is for, not about the org: a runner job in the same org
        // is still handed over.
        await Leases().EnqueueAsync(
            Guid.CreateVersion7(), issueId, "acme/api", Context, DateTimeOffset.UtcNow);
        Assert.NotNull(await Leases().TryClaimAsync(orgId, Now));
    }

    [Fact]
    public async Task A_claimed_job_carries_the_context_it_needs_to_run()
    {
        // Without this the runner has an issue id and nothing to act on, and a fix that reads nothing
        // still produces a confident patch the customer pays for.
        var (orgId, _) = await SeedRunnerJobAsync();

        var job = await Leases().TryClaimAsync(orgId, Now);

        Assert.NotNull(job);
        Assert.Equal(Context.Prompt, job!.Prompt);
        Assert.Equal(Context.BaseBranch, job.BaseBranch);
        Assert.Equal(Context.ScopedPaths, job.ScopedPaths);
    }

    [Fact]
    public async Task The_model_a_runner_used_lands_on_the_run()
    {
        // The runner resolves its own model, so until it reports one the run has none. An empty model
        // would show as blank on the fix and price as if no model had run.
        var (orgId, _) = await SeedRunnerJobAsync();
        var leases = Leases();
        var job = await leases.TryClaimAsync(orgId, Now);
        Assert.NotNull(await leases.TryReportAsync(
            job!.FixId, job.LeaseId, FixStatus.Succeeded, "branch", "url", "done",
            "llama-3.3-70b", 10, 5, Now.AddMinutes(1)));

        var stored = await new PostgresFixStore(pg.ConnectionString).GetAsync(job.FixId);

        Assert.Equal("llama-3.3-70b", stored!.Model);
        Assert.Equal(10, stored.InputTokens);
    }

    [Fact]
    public async Task Only_one_of_two_racing_runners_gets_the_job()
    {
        // The reason the claim is a guarded UPDATE rather than a read followed by a write: both would
        // otherwise see the job as free, and one fix would open two pull requests.
        var (orgId, _) = await SeedRunnerJobAsync();
        var leases = Leases();

        var claims = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => leases.TryClaimAsync(orgId, Now)));

        Assert.Single(claims, c => c is not null);
    }

    [Fact]
    public async Task Work_whose_runner_went_quiet_is_handed_to_another()
    {
        // A customer's machine can die without telling us. Without reclaim the fix sits Running forever.
        var (orgId, _) = await SeedRunnerJobAsync();
        var leases = Leases();
        var first = await leases.TryClaimAsync(orgId, Now);
        Assert.NotNull(first);

        // Still held: nobody else may take it.
        Assert.Null(await leases.TryClaimAsync(orgId, Now.AddMinutes(1)));

        // Past the expiry with no heartbeat: it comes back.
        var second = await leases.TryClaimAsync(orgId, Now + JobLease.Duration + TimeSpan.FromSeconds(1));
        Assert.NotNull(second);
        Assert.NotEqual(first!.LeaseId, second!.LeaseId);
    }

    [Fact]
    public async Task A_runner_cannot_act_on_a_job_it_does_not_hold()
    {
        var (orgId, _) = await SeedRunnerJobAsync();
        var leases = Leases();
        var job = await leases.TryClaimAsync(orgId, Now);
        Assert.NotNull(job);

        // The lease id is the proof. Guessing another value must not work, which is why it is generated
        // here rather than taken from what the caller says its name is.
        Assert.False(await leases.TryHeartbeatAsync(job!.FixId, "not-the-lease", Now.AddMinutes(1)));
        Assert.Null(await leases.TryReportAsync(
            job.FixId, "not-the-lease", FixStatus.Succeeded, "b", "url", "s", "m", 1, 1, Now.AddMinutes(1)));

        Assert.True(await leases.TryHeartbeatAsync(job.FixId, job.LeaseId, Now.AddMinutes(1)));
    }

    [Fact]
    public async Task A_reported_run_is_never_handed_out_again()
    {
        // The failure this guards: a finished run reclaimed and executed a second time, opening a second
        // pull request for one fix.
        var (orgId, _) = await SeedRunnerJobAsync();
        var leases = Leases();
        var job = await leases.TryClaimAsync(orgId, Now);
        Assert.NotNull(await leases.TryReportAsync(
            job!.FixId, job.LeaseId, FixStatus.Succeeded, "branch", "url", "done",
            ModelDefaults.Fix, 10, 5, Now.AddMinutes(1)));

        // Long past any expiry, so only the terminal status keeps it out of the queue.
        Assert.Null(await leases.TryClaimAsync(orgId, Now.AddHours(2)));
    }

    [Fact]
    public async Task A_late_report_from_a_superseded_runner_is_refused()
    {
        var (orgId, _) = await SeedRunnerJobAsync();
        var leases = Leases();
        var first = await leases.TryClaimAsync(orgId, Now);
        var later = Now + JobLease.Duration + TimeSpan.FromSeconds(1);
        var second = await leases.TryClaimAsync(orgId, later);
        Assert.NotNull(second);

        // The first runner finally finishes. Accepting it would overwrite the outcome of the runner that
        // now holds the work.
        Assert.Null(await leases.TryReportAsync(
            first!.FixId, first.LeaseId, FixStatus.Succeeded, "b", "url", "s", "m", 1, 1, later));
    }
}
