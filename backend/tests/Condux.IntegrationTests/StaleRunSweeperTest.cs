extern alias conductor;
using conductor::Condux.Conductor;
using Condux.Core.CveFix;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The sweep that fails the hosted runs a Conductor stop or crash left in progress, against real Postgres:
/// which rows it takes, what it refunds and where, and that two sweeps never refund one run twice.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StaleRunSweeperTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    // A fixed mid-month instant, never the clock, since the sweep and the quota take their instants as
    // parameters and nothing here needs the real time.
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    // In the previous month and older than any cutoff, so a refund dated by when a row was created rather
    // than when it was reserved lands in the wrong month.
    private static readonly DateTimeOffset LastMonth =
        new DateTimeOffset(Now.Year, Now.Month, 1, 0, 0, 0, TimeSpan.Zero).AddDays(-1);

    private const int Limit = 25;

    [Fact]
    public async Task A_run_left_running_past_the_cutoff_is_failed_as_interrupted_and_refunded()
    {
        var (orgId, issueId) = await SeedIssueAsync();
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        var stale = await InsertFixAsync(issueId, FixStatus.Running, LastMonth);

        await Sweeper().SweepAsync(Now, default);

        var run = await Fixes().GetAsync(stale);
        Assert.Equal(FixStatus.Failed, run!.Status);
        Assert.Equal(FixFailureReason.Interrupted, run.FailureReason);
        Assert.Equal(0, await Quota().GetUsedAsync(orgId, Now));
        Assert.Equal(1, await CountAuditAsync(stale, "failed"));
    }

    [Fact]
    public async Task A_late_failure_cannot_undo_the_sweeps_conclusion()
    {
        // A run that outlives the cutoff and then fails reports through the guarded write, which finds it
        // concluded, so the orchestrator neither overwrites the reason nor refunds a second time.
        var (_, issueId) = await SeedIssueAsync();
        var fixId = await InsertFixAsync(issueId, FixStatus.Running, Now.AddDays(-1));
        var (_, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var repo = await new RepoLinkRepository(pg.ConnectionString).LinkAsync(projectId, "acme/api", "main");
        var cves = new PostgresCveFixStore(pg.ConnectionString);
        var bump = Bump(repo.Id, Now.AddDays(-1));
        await cves.InsertAsync(bump);

        await Sweeper().SweepAsync(Now, default);

        var fix = (await Fixes().GetAsync(fixId))!;
        Assert.False(await Fixes().TryFailAsync(fix with { FailureReason = FixFailureReason.TimedOut }));
        Assert.Equal(FixFailureReason.Interrupted, (await Fixes().GetAsync(fixId))!.FailureReason);
        Assert.False(await cves.TryFailAsync(bump with { Status = FixStatus.Failed, FailureReason = FixFailureReason.TimedOut }));
        Assert.Equal(FixFailureReason.Interrupted, Assert.Single(await cves.ListByRepoAsync(repo.Id)).FailureReason);
    }

    [Fact]
    public async Task A_failure_on_a_deleted_run_matches_nothing()
    {
        // A deleted row cannot say whether the sweep refunded it before it went, so it reads as concluded.
        // This pins only that no row means false; the guard itself is pinned by the late-failure test.
        var gone = new FixSuggestion(
            Guid.CreateVersion7(), 1, "acme/api", FixStatus.Failed, "anthropic", "model", "", "", "", Now, Now);

        Assert.False(await Fixes().TryFailAsync(gone));
        Assert.False(await new PostgresCveFixStore(pg.ConnectionString).TryFailAsync(
            Bump(Guid.CreateVersion7(), Now) with { Status = FixStatus.Failed }));
    }

    [Fact]
    public async Task A_failure_on_a_run_in_progress_is_recorded()
    {
        var (_, issueId) = await SeedIssueAsync();
        var fixId = await InsertFixAsync(issueId, FixStatus.Running, Now);
        var fix = (await Fixes().GetAsync(fixId))!;

        Assert.True(await Fixes().TryFailAsync(
            fix with { Status = FixStatus.Failed, FailureReason = FixFailureReason.TimedOut }));
        Assert.Equal(FixStatus.Failed, (await Fixes().GetAsync(fixId))!.Status);
    }

    [Fact]
    public async Task A_pending_run_is_swept_only_past_the_longer_pending_cutoff()
    {
        // A hosted request writes its run pending before it publishes the job, so a pending run may be
        // waiting in the queue behind other runs long after a running one would count as stuck. Past the
        // longer cutoff it is swept, which is also what recovers a job that never reaches the worker.
        var (_, waitingIssue) = await SeedIssueAsync();
        var (orgId, lostIssue) = await SeedIssueAsync();
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        var sweeper = Sweeper();
        var waiting = await InsertFixAsync(
            waitingIssue, FixStatus.Pending, Now - sweeper.PendingStaleAfter + TimeSpan.FromMinutes(1));
        var lost = await InsertFixAsync(
            lostIssue, FixStatus.Pending, Now - sweeper.PendingStaleAfter - TimeSpan.FromMinutes(1));

        await sweeper.SweepAsync(Now, default);

        Assert.True(sweeper.PendingStaleAfter > sweeper.StaleAfter);
        Assert.Equal(FixStatus.Pending, (await Fixes().GetAsync(waiting))!.Status);
        Assert.Equal(FixStatus.Failed, (await Fixes().GetAsync(lost))!.Status);
        Assert.Equal(0, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_pending_cve_bump_is_swept_only_past_the_longer_pending_cutoff()
    {
        var (_, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var links = new RepoLinkRepository(pg.ConnectionString);
        var waitingRepo = await links.LinkAsync(projectId, "acme/api", "main");
        var lostRepo = await links.LinkAsync(projectId, "acme/web", "main");
        var cves = new PostgresCveFixStore(pg.ConnectionString);
        var sweeper = Sweeper();
        await cves.InsertAsync(Bump(waitingRepo.Id, Now - sweeper.PendingStaleAfter + TimeSpan.FromMinutes(1))
            with
        { Status = FixStatus.Pending });
        await cves.InsertAsync(Bump(lostRepo.Id, Now - sweeper.PendingStaleAfter - TimeSpan.FromMinutes(1))
            with
        { Status = FixStatus.Pending });

        await sweeper.SweepAsync(Now, default);

        Assert.Equal(FixStatus.Pending, Assert.Single(await cves.ListByRepoAsync(waitingRepo.Id)).Status);
        Assert.Equal(FixStatus.Failed, Assert.Single(await cves.ListByRepoAsync(lostRepo.Id)).Status);
    }

    [Fact]
    public async Task A_run_inside_the_cutoff_and_a_concluded_run_are_left_alone()
    {
        var (orgId, issueId) = await SeedIssueAsync();
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        var sweeper = Sweeper();
        var live = await InsertFixAsync(issueId, FixStatus.Running, Now - sweeper.StaleAfter + TimeSpan.FromMinutes(1));
        var done = await InsertFixAsync(issueId, FixStatus.Succeeded, Now.AddDays(-1));

        await sweeper.SweepAsync(Now, default);

        Assert.Equal(FixStatus.Running, (await Fixes().GetAsync(live))!.Status);
        Assert.Equal(FixStatus.Succeeded, (await Fixes().GetAsync(done))!.Status);
        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_run_that_took_twice_its_poll_budget_is_still_left_alone()
    {
        // Starting a run, a last slow poll and the stop all fall outside the provider's budget, so the
        // cutoff leaves the budget again plus a margin. This row sits just inside both.
        var (_, issueId) = await SeedIssueAsync();
        var managed = new ManagedAgentOptions(TimeSpan.FromSeconds(10), MaxPolls: 240);
        var slow = await InsertFixAsync(
            issueId, FixStatus.Running, Now - (managed.PollBudget * 2) - TimeSpan.FromMinutes(29));

        await Sweeper(managed).SweepAsync(Now, default);

        Assert.Equal(FixStatus.Running, (await Fixes().GetAsync(slow))!.Status);
    }

    [Fact]
    public async Task A_refund_that_fails_leaves_the_run_in_progress_for_the_next_sweep()
    {
        // The failure and the refund commit together. Were they separate, a refund lost to a shutdown or
        // an error would be lost for good, since a failed row never matches the sweep again.
        var (orgId, issueId) = await SeedIssueAsync();
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        var stale = await InsertFixAsync(issueId, FixStatus.Running, Now.AddDays(-1));
        await ExecuteAsync($"""
            CREATE FUNCTION refuse_refund_{orgId}() RETURNS trigger LANGUAGE plpgsql AS
              $$ BEGIN RAISE EXCEPTION 'refund refused'; END $$;
            CREATE TRIGGER refuse_refund_{orgId} BEFORE UPDATE ON ai_fix_quota
              FOR EACH ROW WHEN (OLD.org_id = {orgId}) EXECUTE FUNCTION refuse_refund_{orgId}();
            """);
        try
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => Sweeper().SweepAsync(Now, default));
            Assert.Equal(FixStatus.Running, (await Fixes().GetAsync(stale))!.Status);
        }
        finally
        {
            await ExecuteAsync($"DROP TRIGGER refuse_refund_{orgId} ON ai_fix_quota; DROP FUNCTION refuse_refund_{orgId}();");
        }

        await Sweeper().SweepAsync(Now, default);

        Assert.Equal(FixStatus.Failed, (await Fixes().GetAsync(stale))!.Status);
        Assert.Equal(0, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_runners_queued_run_is_left_to_its_lease()
    {
        // A runner's row waits pending until a runner claims it, and a lapsed lease already frees it.
        var (orgId, issueId) = await SeedIssueAsync();
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        var queued = Guid.CreateVersion7();
        await new PostgresJobLeaseStore(pg.ConnectionString).EnqueueAsync(
            queued, issueId, "acme/api", new RunnerJobContext("main", "Fix it.", []), Now.AddDays(-1));

        await Sweeper().SweepAsync(Now, default);

        Assert.Equal(FixStatus.Pending, (await Fixes().GetAsync(queued))!.Status);
        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_runners_queued_cve_bump_is_left_to_its_lease()
    {
        var (_, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var repo = await new RepoLinkRepository(pg.ConnectionString).LinkAsync(projectId, "acme/api", "main");
        await new PostgresJobLeaseStore(pg.ConnectionString).EnqueueCveAsync(
            Bump(repo.Id, Now.AddDays(-1)) with { Status = FixStatus.Pending },
            new RunnerJobContext("main", "Bump lodash.", ["package.json"]));

        await Sweeper().SweepAsync(Now, default);

        var run = Assert.Single(await new PostgresCveFixStore(pg.ConnectionString).ListByRepoAsync(repo.Id));
        Assert.Equal(FixStatus.Pending, run.Status);
    }

    [Fact]
    public async Task A_run_reserved_last_month_does_not_refund_this_months_allowance()
    {
        // The counter holds one month. A run reserved before it rolled over was reserved from a month
        // that is gone, so refunding it now would hand out a run this month never reserved.
        var (orgId, issueId) = await SeedIssueAsync();
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        // Created this month, so only the reservation can say the refund belongs to the last one.
        await InsertFixAsync(issueId, FixStatus.Running, Now.AddDays(-1), reservedAt: LastMonth, createdAt: Now);

        await Sweeper().SweepAsync(Now, default);

        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_stale_cve_bump_is_failed_refunded_and_frees_its_advisory()
    {
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var repo = await new RepoLinkRepository(pg.ConnectionString).LinkAsync(projectId, "acme/api", "main");
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        var cves = new PostgresCveFixStore(pg.ConnectionString);
        var stale = Bump(repo.Id, LastMonth);
        await cves.InsertAsync(stale);

        await Sweeper().SweepAsync(Now, default);

        var run = Assert.Single(await cves.ListByRepoAsync(repo.Id));
        Assert.Equal(FixStatus.Failed, run.Status);
        Assert.Equal(FixFailureReason.Interrupted, run.FailureReason);
        Assert.Equal(0, await Quota().GetUsedAsync(orgId, Now));
        // The advisory accepts one run in progress at a time, so a stuck one blocked it for ever.
        Assert.False(await cves.HasActiveRunAsync(repo.Id, stale.GhsaId));
    }

    [Fact]
    public async Task A_second_sweep_neither_fails_nor_refunds_a_run_again()
    {
        // The status guard sits in each UPDATE's WHERE clause, which is also what makes two conductors
        // sweeping at once safe. This proves the guard, sequentially; it does not force an overlap.
        var (orgId, issueId) = await SeedIssueAsync();
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        var stale = await InsertFixAsync(issueId, FixStatus.Running, Now.AddDays(-1));
        var sweep = new PostgresStaleRunSweep(pg.ConnectionString);

        var first = await sweep.FailStaleAsync(Now.AddHours(-1), Now.AddHours(-1), Now, []);
        var second = await sweep.FailStaleAsync(Now.AddHours(-1), Now.AddHours(-1), Now, []);

        Assert.Contains(first, r => r.Id == stale);
        Assert.DoesNotContain(second, r => r.Id == stale);
        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_sweep_also_clears_agent_sessions_past_the_same_cutoff()
    {
        // Nothing polls a session older than the run cutoff, so that is where the session sweep may start.
        var vendor = new VendorStub(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[]}"""),
        });
        var sweeper = Sweeper(sessions: vendor.Sweeper());

        await sweeper.SweepAsync(Now, default);

        var cutoff = Uri.EscapeDataString((Now - sweeper.StaleAfter).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        Assert.Equal(3, vendor.Paths.Count);
        Assert.All(vendor.Paths, p => Assert.Contains($"created_at%5Blt%5D={cutoff}", p, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unreachable_vendor_does_not_stop_the_run_sweep()
    {
        var (orgId, issueId) = await SeedIssueAsync();
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        var stale = await InsertFixAsync(issueId, FixStatus.Running, LastMonth);
        var vendor = new VendorStub(_ => throw new HttpRequestException("vendor unreachable"));

        await Sweeper(sessions: vendor.Sweeper()).SweepAsync(Now, default);

        Assert.Equal(FixStatus.Failed, (await Fixes().GetAsync(stale))!.Status);
        Assert.Equal(0, await Quota().GetUsedAsync(orgId, Now));
        Assert.NotEmpty(vendor.Paths);
    }

    [Fact]
    public async Task An_unreachable_database_does_not_keep_sessions_at_the_vendor()
    {
        var vendor = new VendorStub(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"data":[]}"""),
        });
        var sweeper = new StaleRunSweeper(
            NullLogger<StaleRunSweeper>.Instance,
            new PostgresStaleRunSweep("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=2"),
            Fixes(), new ProjectEventNotifier(pg.ConnectionString), new Condux.Telemetry.ConduxSelfReporter(null),
            ManagedAgentOptions.Default, PullRequests(), vendor.Sweeper());

        await Assert.ThrowsAnyAsync<Exception>(() => sweeper.SweepAsync(Now, default));

        Assert.Equal(3, vendor.Paths.Count);
    }

    // The Managed Agents API as the session sweep sees it: every request recorded, every answer canned.
    private sealed class VendorStub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        // A pinned agent, so the sweep asks only for its sessions and lists no agents.
        public Condux.Agent.ManagedAgents.ManagedAgentsSessionSweeper Sweeper()
        {
            var options = new Condux.Agent.ManagedAgents.ManagedAgentsOptions("test-key", 5m) { AgentId = "agent_1" };
            return new(new Condux.Agent.ManagedAgents.ManagedAgentsClient(new HttpClient(this), options), options);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(answer(request));
        }
    }

    private StaleRunSweeper Sweeper(
        ManagedAgentOptions? options = null, Condux.Agent.ManagedAgents.ManagedAgentsSessionSweeper? sessions = null) => new(
        NullLogger<StaleRunSweeper>.Instance, new PostgresStaleRunSweep(pg.ConnectionString), Fixes(),
        new ProjectEventNotifier(pg.ConnectionString), new Condux.Telemetry.ConduxSelfReporter(null),
        options ?? ManagedAgentOptions.Default, PullRequests(), sessions);

    // No source host, as for the providers that cannot open a pull request, so every stale run is cleared.
    private UnrecordedPullRequests PullRequests() => new(
        NullLogger<UnrecordedPullRequests>.Instance, new PostgresStaleRunSweep(pg.ConnectionString), Fixes(),
        new ProjectEventNotifier(pg.ConnectionString));

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private PostgresAiFixQuota Quota() => new(pg.ConnectionString);

    private PostgresFixStore Fixes() => new(pg.ConnectionString);

    private async Task<(long OrgId, long IssueId)> SeedIssueAsync()
    {
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping($"fp-{Guid.NewGuid():N}", "TypeError: boom", "run"), Level.Error, Now);
        return (orgId, issue.Id);
    }

    // The orchestrator last writes the row when it marks it running, and not again while the provider
    // runs, so a run's age is its updated_at (archiving it also stamps it, which only delays a sweep). The
    // helper stamps both columns alike unless a test says otherwise. The reservation is dated Now unless a test says
    // otherwise, like the counter it came from, so a refund lands in this month on any day of it.
    private async Task<Guid> InsertFixAsync(
        long issueId, FixStatus status, DateTimeOffset at, DateTimeOffset? reservedAt = null,
        DateTimeOffset? createdAt = null)
    {
        var id = Guid.CreateVersion7();
        // An issue holds one run in progress at a time, and a refused insert writes nothing, so a test that
        // seeded two would otherwise go on to assert about a row that is not there.
        Assert.True(await Fixes().InsertAsync(new FixSuggestion(
            id, issueId, "acme/api", status, "anthropic", "model", "", "", "", createdAt ?? at, at)
        { ReservedAt = reservedAt ?? Now }));
        return id;
    }

    private static CveFixRun Bump(Guid repoLinkId, DateTimeOffset at) => new(
        Guid.CreateVersion7(), repoLinkId, "GHSA-jf85-cpcp-j695", "CVE-2021-23337", "lodash", "npm",
        "< 4.17.21", "4.17.21", "https://example.invalid/advisory", FixStatus.Running, "anthropic", "model",
        Branch: "", PrUrl: "", Summary: "", Actor: "42", at, at)
    { ReservedAt = Now };

    private async Task<long> CountAuditAsync(Guid fixId, string eventName)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT count(*) FROM fix_audit WHERE fix_id = @fix AND event = @event", conn);
        cmd.Parameters.AddWithValue("fix", fixId);
        cmd.Parameters.AddWithValue("event", eventName);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
