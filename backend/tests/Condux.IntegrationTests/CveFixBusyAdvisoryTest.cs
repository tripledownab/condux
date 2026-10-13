using Condux.Core.CveFix;
using Condux.Core.FixEngine;
using Condux.Core.Plans;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// A second bump for an advisory that already has one in progress, against real Postgres. The request path
/// checks for an active run, but a hosted run's row is written only when the conductor takes the job, so
/// two requests close together both pass that check and both reserve. The in-progress index admits one of
/// them, and the other's reservation has to come back exactly once.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CveFixBusyAdvisoryTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly int FreeLimit = PlanCatalog.For(Tier.Free).AiFixesPerMonth;

    [Fact]
    public async Task A_second_hosted_bump_for_a_busy_advisory_is_not_run_and_is_refunded()
    {
        var (orgId, repoId) = await SeedRepoAsync();
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        Assert.True(await Store().InsertAsync(Bump(repoId) with { JobId = Guid.CreateVersion7() }));

        Assert.False(await Store().InsertAsync(Bump(repoId) with { JobId = Guid.CreateVersion7() }));

        Assert.Single(await Store().ListByRepoAsync(repoId));
        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_job_delivered_again_to_a_busy_advisory_is_not_refunded()
    {
        // The same job's row is what makes the advisory busy, and that run still holds the reservation.
        var (orgId, repoId) = await SeedRepoAsync();
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        var first = Bump(repoId) with { JobId = Guid.CreateVersion7() };
        Assert.True(await Store().InsertAsync(first));

        Assert.False(await Store().InsertAsync(first with { Id = Guid.CreateVersion7() }));

        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_job_with_no_id_meeting_a_busy_advisory_is_not_refunded()
    {
        // Queued before jobs carried an id, it cannot be told apart from a delivery of the job that holds
        // the advisory, so refunding it could hand out a run nobody reserved.
        var (orgId, repoId) = await SeedRepoAsync();
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        Assert.True(await Store().InsertAsync(Bump(repoId)));

        Assert.False(await Store().InsertAsync(Bump(repoId)));

        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_refused_job_delivered_again_is_refunded_once()
    {
        // Refused, it writes no run, so only its skip record stops a second delivery refunding again.
        var (orgId, repoId) = await SeedRepoAsync();
        for (var i = 0; i < 3; i++)
        {
            await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        }
        Assert.True(await Store().InsertAsync(Bump(repoId) with { JobId = Guid.CreateVersion7() }));
        var refused = Bump(repoId) with { JobId = Guid.CreateVersion7() };

        Assert.False(await Store().InsertAsync(refused));
        Assert.False(await Store().InsertAsync(refused with { Id = Guid.CreateVersion7() }));

        Assert.Equal(2, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_refused_job_is_refunded_to_the_month_it_was_reserved_in()
    {
        // Reserved this month and taken by a conductor whose clock says last month: the refund follows the
        // reservation, so this month's counter loses the run. Refunding by creation, or not at all, keeps it.
        var (orgId, repoId) = await SeedRepoAsync();
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        Assert.True(await Store().InsertAsync(Bump(repoId) with { JobId = Guid.CreateVersion7() }));

        Assert.False(await Store().InsertAsync(
            Bump(repoId) with { JobId = Guid.CreateVersion7(), CreatedAt = Now.AddMonths(-1) }));

        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_refused_job_delivered_after_the_advisory_frees_up_does_not_run()
    {
        // It handed its reservation back when it was refused, so running it now would be a run nobody paid
        // for, and failing it would refund a second time.
        var (orgId, repoId) = await SeedRepoAsync();
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        var holder = Bump(repoId) with { JobId = Guid.CreateVersion7() };
        Assert.True(await Store().InsertAsync(holder));
        var refused = Bump(repoId) with { JobId = Guid.CreateVersion7() };
        Assert.False(await Store().InsertAsync(refused));
        await Store().RecordSuccessAsync(holder with { Status = FixStatus.Succeeded, PrUrl = "https://example.invalid/p/1" });

        Assert.False(await Store().InsertAsync(refused with { Id = Guid.CreateVersion7() }));

        Assert.Single(await Store().ListByRepoAsync(repoId));
        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_second_runner_bump_for_a_busy_advisory_is_refused()
    {
        var (_, repoId) = await SeedRepoAsync();
        var leases = new PostgresJobLeaseStore(pg.ConnectionString);
        var context = new RunnerJobContext("main", "Bump lodash.", ["package.json"]);

        Assert.True(await leases.EnqueueCveAsync(Bump(repoId) with { Status = FixStatus.Pending }, context));
        Assert.False(await leases.EnqueueCveAsync(Bump(repoId) with { Status = FixStatus.Pending }, context));

        Assert.Single(await Store().ListByRepoAsync(repoId));
    }

    private PostgresAiFixQuota Quota() => new(pg.ConnectionString);

    private PostgresCveFixStore Store() => new(pg.ConnectionString);

    private async Task<(long OrgId, Guid RepoId)> SeedRepoAsync()
    {
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var repo = await new RepoLinkRepository(pg.ConnectionString).LinkAsync(projectId, "acme/api", "main");
        return (orgId, repo.Id);
    }

    private static CveFixRun Bump(Guid repoLinkId) => new(
        Guid.CreateVersion7(), repoLinkId, "GHSA-jf85-cpcp-j695", "CVE-2021-23337", "lodash", "npm",
        "< 4.17.21", "4.17.21", "https://example.invalid/advisory", FixStatus.Running, "anthropic", "model",
        Branch: "", PrUrl: "", Summary: "", Actor: "42", Now, Now)
    { ReservedAt = Now };
}
