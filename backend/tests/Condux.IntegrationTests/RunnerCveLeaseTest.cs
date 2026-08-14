using Condux.Core.CveFix;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The CVE side of the lease against real Postgres: the one claim serves both run kinds, so what
/// matters here is the seams between them — that a hosted bump is never handed out, that a bump's
/// outcome lands in cve_fix_runs and not the fix table, and that a runner's bump spend stays the
/// customer's own. The concurrency guarantees (racing claims, lapsed leases, refused late reports)
/// share their SQL shape with the issue-fix side and are pinned by <see cref="RunnerLeaseTest"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunnerCveLeaseTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private const string Ghsa = "GHSA-jf85-cpcp-j695";

    private static readonly RunnerJobContext Context =
        new("main", "Bump lodash to 4.17.21 to remediate GHSA-jf85-cpcp-j695.", ["package.json"]);

    private PostgresJobLeaseStore Leases() => new(pg.ConnectionString);

    private async Task<(long OrgId, long ProjectId, Guid RepoLinkId)> SeedRepoAsync()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var repo = await new RepoLinkRepository(pg.ConnectionString)
            .LinkAsync(projectId, "acme/api", "main");
        return (orgId, projectId, repo.Id);
    }

    private static CveFixRun BumpRun(Guid repoLinkId) => new(
        Guid.CreateVersion7(), repoLinkId, Ghsa, "CVE-2021-23337", "lodash", "npm",
        "< 4.17.21", "4.17.21", "https://example.invalid/advisory", FixStatus.Pending,
        PostgresJobLeaseStore.RunnerProvider, Model: "", Branch: "", PrUrl: "", Summary: "",
        Actor: "42", Now, Now);

    [Fact]
    public async Task A_claimed_bump_carries_its_context_and_names_the_advisory()
    {
        var (orgId, projectId, repoLinkId) = await SeedRepoAsync();
        await Leases().EnqueueCveAsync(BumpRun(repoLinkId), Context);

        var job = await Leases().TryClaimAsync(orgId, Now);

        Assert.NotNull(job);
        Assert.Equal(JobKind.CveFix, job!.Kind);
        // The branch and pull request are named after the advisory — a bump has no issue number, and
        // "fix-0" on a customer's repo would read as a broken product.
        Assert.Equal(Ghsa, job.Ref);
        Assert.Equal(0, job.IssueId);
        Assert.Equal(projectId, job.ProjectId);
        Assert.Equal("acme/api", job.RepoFullName);
        Assert.Equal(Context.Prompt, job.Prompt);
        Assert.Equal(Context.ScopedPaths, job.ScopedPaths);
    }

    [Fact]
    public async Task A_bump_the_hosted_conductor_is_executing_is_never_claimable()
    {
        // Same failure the issue-fix condition exists for: the hosted CVE orchestrator flips its run to
        // Running and never takes a lease, so without the job_context condition a runner would take work
        // already under way and open a second bump pull request for one advisory.
        var (orgId, _, repoLinkId) = await SeedRepoAsync();
        await new PostgresCveFixStore(pg.ConnectionString)
            .InsertAsync(BumpRun(repoLinkId) with { Status = FixStatus.Running, Provider = "anthropic" });

        Assert.Null(await Leases().TryClaimAsync(orgId, Now));
    }

    [Fact]
    public async Task A_reported_bump_lands_on_its_run_row_and_is_never_handed_out_again()
    {
        var (orgId, projectId, repoLinkId) = await SeedRepoAsync();
        var leases = Leases();
        await leases.EnqueueCveAsync(BumpRun(repoLinkId), Context);
        var job = await leases.TryClaimAsync(orgId, Now);

        var reported = await leases.TryReportAsync(
            job!.FixId, job.LeaseId, FixStatus.Succeeded, "condux/fix-ghsa", "https://example.invalid/p/1",
            "Bumped lodash.", "llama-3.3-70b", 10, 5, Now.AddMinutes(1));

        // The caller needs the kind to know there is no fix_audit trail to append to, and the project
        // for the live-badge nudge.
        Assert.Equal(new ReportedJob(projectId, JobKind.CveFix), reported);
        var run = Assert.Single(await new PostgresCveFixStore(pg.ConnectionString).ListByRepoAsync(repoLinkId));
        Assert.Equal(FixStatus.Succeeded, run.Status);
        Assert.Equal("https://example.invalid/p/1", run.PrUrl);
        Assert.Equal("llama-3.3-70b", run.Model);
        Assert.Equal(10, run.InputTokens);
        // Terminal: long past any lease expiry, only the status keeps it out of the queue.
        Assert.Null(await leases.TryClaimAsync(orgId, Now.AddHours(2)));
    }

    [Fact]
    public async Task An_issue_fix_is_offered_before_a_bump()
    {
        // Deliberate ordering, not an accident of SQL: an issue has a person or an alert behind it,
        // a dependency bump can wait one more poll.
        var (orgId, projectId, repoLinkId) = await SeedRepoAsync();
        var leases = Leases();
        await leases.EnqueueCveAsync(BumpRun(repoLinkId), Context);
        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping($"fp-{Guid.NewGuid():N}", "ValueError: boom", "run"),
            Level.Error, DateTimeOffset.UtcNow);
        await leases.EnqueueAsync(Guid.CreateVersion7(), issue.Id, "acme/api", Context, Now);

        var first = await leases.TryClaimAsync(orgId, Now);
        var second = await leases.TryClaimAsync(orgId, Now);

        Assert.Equal(JobKind.IssueFix, first!.Kind);
        Assert.Equal(JobKind.CveFix, second!.Kind);
    }

    [Fact]
    public async Task A_runner_bump_does_not_count_against_the_org_spend()
    {
        // The cost cap governs spend on OUR compute (ADR-0033 slice 4c). A bump executed on the org's
        // own runner billed their own model account; counting those tokens here would refuse hosted
        // runs over money we never paid.
        var (orgId, _, repoLinkId) = await SeedRepoAsync();
        var leases = Leases();
        await leases.EnqueueCveAsync(BumpRun(repoLinkId), Context);
        var job = await leases.TryClaimAsync(orgId, Now);
        Assert.NotNull(await leases.TryReportAsync(
            job!.FixId, job.LeaseId, FixStatus.Succeeded, "b", "https://example.invalid/p/1", "s",
            "claude-opus-4-8", 1_000_000, 1_000_000, Now.AddMinutes(1)));

        Assert.Equal(0m, await new PostgresAiFixSpend(pg.ConnectionString)
            .MonthToDateUsdAsync(orgId, Now.AddMinutes(2)));
    }
}
