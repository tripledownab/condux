using Condux.Core.CveFix;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.Core.Plans;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// How a fix run's conclusion moves the org's allowance, against real Postgres: a failure refunds the
/// month the run was reserved in and commits with it, a job delivered again is refused by its id, and a run
/// the stale-run sweep refunded is charged again when it succeeds after all, never past the plan limit.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunConclusionTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    // Always in the previous month and always older than the sweep cutoffs used here, so a conclusion that
    // read the creation time instead of the reservation would land in the wrong month on any day.
    private static readonly DateTimeOffset LastMonth =
        new DateTimeOffset(Now.Year, Now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddDays(-1);

    // A seeded org is on the free tier, whose monthly allowance this is.
    private static readonly int FreeLimit = PlanCatalog.For(Tier.Free).AiFixesPerMonth;

    [Fact]
    public async Task A_failure_refunds_the_month_the_run_was_reserved_in_not_the_month_it_was_created_in()
    {
        var (orgId, issueId) = await SeedIssueAsync();
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        var reservedLastMonth = await InsertFixAsync(issueId, createdAt: Now, reservedAt: LastMonth);
        var reservedThisMonth = await InsertFixAsync(
            await SeedSiblingIssueAsync(issueId), createdAt: LastMonth, reservedAt: Now);

        Assert.True(await Fixes().TryFailAsync(Failed(await GetAsync(reservedLastMonth))));
        Assert.Equal(2, await Quota().GetUsedAsync(orgId, Now));

        Assert.True(await Fixes().TryFailAsync(Failed(await GetAsync(reservedThisMonth))));
        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_failure_whose_refund_fails_is_not_recorded_either()
    {
        // Two commits would let a crash between them keep a failed run's reservation for good, since a
        // failed row never matches the failure write again.
        var (orgId, issueId) = await SeedIssueAsync();
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        var fixId = await InsertFixAsync(issueId, createdAt: Now, reservedAt: Now);
        var failed = Failed(await GetAsync(fixId));

        await RefusingRefundsAsync(orgId, async () =>
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Fixes().TryFailAsync(failed)));

        Assert.Equal(FixStatus.Running, (await GetAsync(fixId)).Status);
        Assert.True(await Fixes().TryFailAsync(Failed(await GetAsync(fixId))));
        Assert.Equal(0, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_job_delivered_again_is_refused_by_its_id_and_a_job_with_none_never_is()
    {
        // Each run on its own issue, which holds one run in progress at a time, so only the job id can be
        // what refuses the second.
        var (_, issueId) = await SeedIssueAsync();
        var jobId = Guid.CreateVersion7();

        Assert.True(await Fixes().InsertAsync(Run(issueId, Now, Now) with { JobId = jobId }));
        Assert.False(await Fixes().InsertAsync(Run(await SeedSiblingIssueAsync(issueId), Now, Now) with { JobId = jobId }));
        Assert.True(await Fixes().InsertAsync(Run(await SeedSiblingIssueAsync(issueId), Now, null)));
        Assert.True(await Fixes().InsertAsync(Run(await SeedSiblingIssueAsync(issueId), Now, null)));
    }

    [Fact]
    public async Task A_run_the_sweep_refunded_is_charged_again_when_it_succeeds()
    {
        var (orgId, issueId) = await SeedIssueAsync();
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        var fixId = await InsertFixAsync(issueId, createdAt: LastMonth, reservedAt: Now);
        await new PostgresStaleRunSweep(pg.ConnectionString).FailStaleAsync(Now.AddHours(-1), Now.AddHours(-1), Now, []);
        Assert.Equal(0, await Quota().GetUsedAsync(orgId, Now));

        await Fixes().RecordSuccessAsync(Succeeded(await GetAsync(fixId)));

        Assert.Equal(FixStatus.Succeeded, (await GetAsync(fixId)).Status);
        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_charge_back_never_takes_the_org_past_its_limit()
    {
        // The slot the sweep freed may already be spent on another run, and the org must not be blocked
        // from runs it was told it had, nor see more used than its plan holds.
        var (orgId, issueId) = await SeedIssueAsync();
        for (var i = 0; i < FreeLimit; i++)
        {
            await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        }
        var fixId = await InsertFixAsync(issueId, createdAt: LastMonth, reservedAt: Now);
        await new PostgresStaleRunSweep(pg.ConnectionString).FailStaleAsync(Now.AddHours(-1), Now.AddHours(-1), Now, []);
        Assert.True(await Quota().TryConsumeAsync(orgId, FreeLimit, Now));

        await Fixes().RecordSuccessAsync(Succeeded(await GetAsync(fixId)));

        Assert.Equal(FreeLimit, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_success_after_a_failure_the_sweep_did_not_write_is_not_charged_again()
    {
        // Only the sweep refunds a run that may still succeed. A re-charge keyed on any failure would take
        // back a refund the run earned by failing on its own.
        var (orgId, issueId) = await SeedIssueAsync();
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        var fixId = await InsertFixAsync(issueId, createdAt: Now, reservedAt: Now);
        await Fixes().TryFailAsync(Failed(await GetAsync(fixId)));

        await Fixes().RecordSuccessAsync(Succeeded(await GetAsync(fixId)));

        Assert.Equal(0, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_run_the_sweep_concluded_is_not_marked_running_again()
    {
        // Written back to running, a later failure would find it in progress and refund it a second time.
        var (orgId, issueId) = await SeedIssueAsync();
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        var run = Run(issueId, LastMonth, Now) with { Status = FixStatus.Pending };
        await Fixes().InsertAsync(run);
        await new PostgresStaleRunSweep(pg.ConnectionString).FailStaleAsync(Now.AddHours(-1), Now.AddHours(-1), Now, []);

        await Fixes().MarkRunningAsync(run with { Status = FixStatus.Running });

        Assert.Equal(FixStatus.Failed, (await GetAsync(run.Id)).Status);
        Assert.False(await Fixes().TryFailAsync(Failed(run)));
        Assert.Equal(0, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_success_that_waits_on_the_sweep_sees_what_the_sweep_wrote()
    {
        // The success reads the run's state under its row lock. Read without it, a sweep committing in
        // between leaves the success holding a stale "running", so it overwrites the sweep's refund and
        // never charges the run back. The sweep here is held open while the success starts.
        var (orgId, issueId) = await SeedIssueAsync();
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        var fixId = await InsertFixAsync(issueId, createdAt: Now, reservedAt: Now);
        var succeeded = Succeeded(await GetAsync(fixId));

        await using var sweep = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await sweep.OpenAsync();
        await using var tx = await sweep.BeginTransactionAsync();
        await using (var fail = new Npgsql.NpgsqlCommand(
            "UPDATE fix_suggestions SET status = @failed, failure_reason = @reason WHERE id = @id;"
            + "UPDATE ai_fix_quota SET used = used - 1 WHERE org_id = @org;", sweep, tx))
        {
            fail.Parameters.AddWithValue("failed", (short)FixStatus.Failed);
            fail.Parameters.AddWithValue("reason", (short)FixFailureReason.Interrupted);
            fail.Parameters.AddWithValue("id", fixId);
            fail.Parameters.AddWithValue("org", orgId);
            await fail.ExecuteNonQueryAsync();
        }

        var success = Fixes().RecordSuccessAsync(succeeded);
        await WaitUntilBlockedByAsync(sweep.ProcessID);
        await tx.CommitAsync();
        await success;

        Assert.Equal(FixStatus.Succeeded, (await GetAsync(fixId)).Status);
        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));
    }

    // Polls the server for a backend that the given one is blocking, rather than sleeping, so the test
    // knows the success has reached the row the open transaction holds.
    private async Task WaitUntilBlockedByAsync(int holderPid)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE @holder = ANY(pg_blocking_pids(pid))", conn);
        cmd.Parameters.AddWithValue("holder", holderPid);
        while ((long)(await cmd.ExecuteScalarAsync())! == 0)
        {
            Assert.True(DateTimeOffset.UtcNow < deadline, "the success never waited on the held row");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task A_cve_bump_concludes_the_same_way()
    {
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var repo = await new RepoLinkRepository(pg.ConnectionString).LinkAsync(projectId, "acme/api", "main");
        var cves = new PostgresCveFixStore(pg.ConnectionString);
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        var jobId = Guid.CreateVersion7();
        var failing = Bump(repo.Id, Now, LastMonth) with { JobId = jobId };
        var swept = Bump(repo.Id, LastMonth, Now) with { GhsaId = "GHSA-aaaa-bbbb-cccc" };

        Assert.True(await cves.InsertAsync(failing));
        Assert.False(await cves.InsertAsync(Bump(repo.Id, Now, Now) with { JobId = jobId }));
        Assert.True(await cves.InsertAsync(swept));

        // Reserved last month, so the refund leaves this month's counter alone.
        Assert.True(await cves.TryFailAsync(failing with { Status = FixStatus.Failed }));
        Assert.Equal(2, await Quota().GetUsedAsync(orgId, Now));

        await new PostgresStaleRunSweep(pg.ConnectionString).FailStaleAsync(Now.AddHours(-1), Now.AddHours(-1), Now, []);
        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));
        await cves.RecordSuccessAsync(swept with { Status = FixStatus.Succeeded, PrUrl = "https://example.invalid/p/1" });
        Assert.Equal(2, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_runners_failure_report_refunds_with_it_or_not_at_all()
    {
        var (orgId, issueId) = await SeedIssueAsync();
        var leases = new PostgresJobLeaseStore(pg.ConnectionString);
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        await leases.EnqueueAsync(
            Guid.CreateVersion7(), issueId, "acme/api", new RunnerJobContext("main", "Fix it.", []), Now);
        var job = (await leases.TryClaimAsync(orgId, Now))!;

        Task<ReportedJob?> ReportAsync() => leases.TryReportAsync(
            job.FixId, job.LeaseId, FixStatus.Failed, "", "", "", "", 0, 0, Now,
            failureReason: FixFailureReason.TimedOut);

        await RefusingRefundsAsync(orgId, async () =>
            await Assert.ThrowsAsync<Npgsql.PostgresException>(ReportAsync));
        Assert.Equal(FixStatus.Running, (await GetAsync(job.FixId)).Status);
        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));

        Assert.NotNull(await ReportAsync());
        Assert.Equal(FixStatus.Failed, (await GetAsync(job.FixId)).Status);
        Assert.Equal(0, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_runners_failed_bump_refunds_the_month_it_was_reserved_in()
    {
        // Its row is dated last month, as a job whose reservation was taken this month never is, so only
        // the reservation can put the refund in this month.
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var repo = await new RepoLinkRepository(pg.ConnectionString).LinkAsync(projectId, "acme/api", "main");
        var leases = new PostgresJobLeaseStore(pg.ConnectionString);
        await Quota().TryConsumeAsync(orgId, FreeLimit, Now);
        await leases.EnqueueCveAsync(
            Bump(repo.Id, LastMonth, Now) with { Status = FixStatus.Pending },
            new RunnerJobContext("main", "Bump lodash.", ["package.json"]));
        var job = (await leases.TryClaimAsync(orgId, Now))!;

        Assert.NotNull(await leases.TryReportAsync(
            job.FixId, job.LeaseId, FixStatus.Failed, "", "", "", "", 0, 0, Now,
            failureReason: FixFailureReason.TimedOut));

        Assert.Equal(0, await Quota().GetUsedAsync(orgId, Now));
    }

    // Makes every allowance write for this org fail while the action runs, as a lost connection would.
    private async Task RefusingRefundsAsync(long orgId, Func<Task> action)
    {
        await ExecuteAsync($"""
            CREATE FUNCTION refuse_refund_{orgId}() RETURNS trigger LANGUAGE plpgsql AS
              $$ BEGIN RAISE EXCEPTION 'refund refused'; END $$;
            CREATE TRIGGER refuse_refund_{orgId} BEFORE UPDATE ON ai_fix_quota
              FOR EACH ROW WHEN (OLD.org_id = {orgId}) EXECUTE FUNCTION refuse_refund_{orgId}();
            """);
        try
        {
            await action();
        }
        finally
        {
            await ExecuteAsync($"DROP TRIGGER refuse_refund_{orgId} ON ai_fix_quota; DROP FUNCTION refuse_refund_{orgId}();");
        }
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private PostgresAiFixQuota Quota() => new(pg.ConnectionString);

    private PostgresFixStore Fixes() => new(pg.ConnectionString);

    private async Task<FixSuggestion> GetAsync(Guid fixId) =>
        await Fixes().GetAsync(fixId) ?? throw new InvalidOperationException("the run is gone");

    private static FixSuggestion Failed(FixSuggestion s) =>
        s with { Status = FixStatus.Failed, FailureReason = FixFailureReason.TimedOut };

    private static FixSuggestion Succeeded(FixSuggestion s) =>
        s with { Status = FixStatus.Succeeded, FailureReason = null, PrUrl = "https://example.invalid/p/1" };

    private async Task<(long OrgId, long IssueId)> SeedIssueAsync()
    {
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping($"fp-{Guid.NewGuid():N}", "TypeError: boom", "run"), Level.Error, Now);
        return (orgId, issue.Id);
    }

    // Another issue in the same project and org, for a second run in progress: an issue holds one at a time.
    private async Task<long> SeedSiblingIssueAsync(long issueId)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT project_id FROM issues WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", issueId);
        var projectId = (long)(await cmd.ExecuteScalarAsync())!;
        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping($"fp-{Guid.NewGuid():N}", "TypeError: boom", "run"), Level.Error, Now);
        return issue.Id;
    }

    private async Task<Guid> InsertFixAsync(long issueId, DateTimeOffset createdAt, DateTimeOffset reservedAt)
    {
        var run = Run(issueId, createdAt, reservedAt);
        await Fixes().InsertAsync(run);
        return run.Id;
    }

    private static FixSuggestion Run(long issueId, DateTimeOffset createdAt, DateTimeOffset? reservedAt) =>
        new(Guid.CreateVersion7(), issueId, "acme/api", FixStatus.Running, "anthropic", "model", "", "", "",
            createdAt, createdAt)
        { ReservedAt = reservedAt };

    private static CveFixRun Bump(Guid repoLinkId, DateTimeOffset createdAt, DateTimeOffset reservedAt) =>
        new(Guid.CreateVersion7(), repoLinkId, "GHSA-jf85-cpcp-j695", "CVE-2021-23337", "lodash", "npm",
            "< 4.17.21", "4.17.21", "https://example.invalid/advisory", FixStatus.Running, "anthropic", "model",
            Branch: "", PrUrl: "", Summary: "", Actor: "42", createdAt, createdAt)
        { ReservedAt = reservedAt };
}
