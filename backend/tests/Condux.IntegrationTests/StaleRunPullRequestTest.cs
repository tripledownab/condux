extern alias conductor;
using conductor::Condux.Conductor;
using Condux.Core.CveFix;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.Core.SourceControl;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The stale-run sweep asking the source host before it fails a running run, against real Postgres: a run
/// that opened its pull request and then stopped is recorded as the success it was, not failed and refunded,
/// and a run the host cannot be asked about is left for a later pass until the pending cutoff.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StaleRunPullRequestTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private const int Limit = 25;
    private const string PrUrl = "https://github.test/acme/api/pull/7";
    private static readonly FoundPullRequest Open = new(PrUrl, MergedAt: null);

    [Fact]
    public async Task A_stopped_run_whose_pull_request_exists_is_recorded_as_succeeded_and_kept_charged()
    {
        var (orgId, issueId) = await SeedIssueAsync(withInstallation: true);
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        var fixId = await InsertRunningFixAsync(issueId);
        var branch = FixBranches.ToBranch(issueId.ToString(), fixId);
        var host = new SourceHost(found: new() { [branch] = Open });

        await Sweeper(host).SweepAsync(Now, default);

        var run = await Fixes().GetAsync(fixId);
        Assert.Equal(FixStatus.Succeeded, run!.Status);
        Assert.Equal(PrUrl, run.PrUrl);
        Assert.Equal(branch, run.Branch);
        Assert.Null(run.FailureReason);
        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));
        Assert.Equal(1, await CountAuditAsync(fixId, "draft_pr_found"));
        Assert.Equal(0, await CountAuditAsync(fixId, "failed"));
        Assert.Contains(("acme/api", branch), host.Asked);
    }

    [Fact]
    public async Task A_found_pull_request_already_merged_starts_the_verification_watch()
    {
        // Its merge webhook fired while the run was still marked running and matched nothing.
        var (_, issueId) = await SeedIssueAsync(withInstallation: true);
        var fixId = await InsertRunningFixAsync(issueId);
        var branch = FixBranches.ToBranch(issueId.ToString(), fixId);
        var mergedAt = Now.AddHours(-2);

        await Sweeper(new SourceHost(found: new() { [branch] = Open with { MergedAt = mergedAt } }))
            .SweepAsync(Now, default);

        var watching = await new PostgresFixVerification(pg.ConnectionString).ListWatchingAsync();
        Assert.Equal(mergedAt, Assert.Single(watching, w => w.FixId == fixId).MergedAt);
        Assert.Equal(1, await CountAuditAsync(fixId, "fix_merged"));
    }

    [Fact]
    public async Task A_lookup_that_fails_outright_does_not_stop_the_pending_sweep()
    {
        var (orgId, issueId) = await SeedIssueAsync(withInstallation: true);
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        var sweeper = Sweeper(
            new SourceHost(found: []), lookupConnection: "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=2");
        var stamp = Now - sweeper.PendingStaleAfter - TimeSpan.FromMinutes(1);
        var pending = Guid.CreateVersion7();
        Assert.True(await Fixes().InsertAsync(new FixSuggestion(
            pending, issueId, "acme/api", FixStatus.Pending, "", "model", "", "", "", stamp, stamp)
        { ReservedAt = Now }));

        await sweeper.SweepAsync(Now, default);

        Assert.Equal(FixStatus.Failed, (await Fixes().GetAsync(pending))!.Status);
        Assert.Equal(0, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_stopped_run_with_no_pull_request_is_failed_and_refunded()
    {
        var (orgId, issueId) = await SeedIssueAsync(withInstallation: true);
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        var fixId = await InsertRunningFixAsync(issueId);
        var host = new SourceHost(found: []);

        await Sweeper(host).SweepAsync(Now, default);

        Assert.Equal(FixStatus.Failed, (await Fixes().GetAsync(fixId))!.Status);
        Assert.Equal(0, await Quota().GetUsedAsync(orgId, Now));
        Assert.Single(host.Asked, a => a.Branch == FixBranches.ToBranch(issueId.ToString(), fixId));
    }

    [Fact]
    public async Task A_run_the_host_cannot_be_asked_about_stays_running_until_the_pending_cutoff()
    {
        var (orgId, issueId) = await SeedIssueAsync(withInstallation: true);
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        var fixId = await InsertRunningFixAsync(issueId);
        var sweeper = Sweeper(new SourceHost(found: [], fails: true));

        await sweeper.SweepAsync(Now, default);

        Assert.Equal(FixStatus.Running, (await Fixes().GetAsync(fixId))!.Status);
        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));

        // A day on, a host that still cannot answer no longer holds the allowance.
        await sweeper.SweepAsync(Now + sweeper.PendingStaleAfter, default);

        Assert.Equal(FixStatus.Failed, (await Fixes().GetAsync(fixId))!.Status);
        Assert.Equal(0, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_run_in_an_org_with_no_installation_is_not_failed_before_the_pending_cutoff()
    {
        var (orgId, issueId) = await SeedIssueAsync(withInstallation: false);
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        var fixId = await InsertRunningFixAsync(issueId);
        var host = new SourceHost(found: []);

        await Sweeper(host).SweepAsync(Now, default);

        Assert.Equal(FixStatus.Running, (await Fixes().GetAsync(fixId))!.Status);
        Assert.Empty(host.Asked);
    }

    [Fact]
    public async Task A_stopped_bump_whose_pull_request_exists_is_recorded_as_succeeded()
    {
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        await new GithubInstallationRepository(pg.ConnectionString).LinkAsync(InstallationIdFor(orgId), orgId);
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        var repo = await new RepoLinkRepository(pg.ConnectionString).LinkAsync(projectId, "acme/api", "main");
        var cves = new PostgresCveFixStore(pg.ConnectionString);
        var bump = Bump(repo.Id);
        await cves.InsertAsync(bump);
        var branch = FixBranches.ToBranch(bump.GhsaId, bump.Id);

        await Sweeper(new SourceHost(found: new() { [branch] = Open })).SweepAsync(Now, default);

        var run = (await cves.ListByRepoAsync(repo.Id)).Single(r => r.Id == bump.Id);
        Assert.Equal(FixStatus.Succeeded, run.Status);
        Assert.Equal(PrUrl, run.PrUrl);
        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));
    }

    /// <summary>
    /// A bump whose pull request merged while it still read as running is watched (ADR-0051), as an issue
    /// fix is: its merge webhook matched nothing, and nothing else would ever mark it.
    /// </summary>
    [Fact]
    public async Task A_found_bump_pull_request_already_merged_starts_the_verification_watch()
    {
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        await new GithubInstallationRepository(pg.ConnectionString).LinkAsync(InstallationIdFor(orgId), orgId);
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        var repo = await new RepoLinkRepository(pg.ConnectionString).LinkAsync(projectId, "acme/api", "main");
        var bump = Bump(repo.Id);
        await new PostgresCveFixStore(pg.ConnectionString).InsertAsync(bump);
        var branch = FixBranches.ToBranch(bump.GhsaId, bump.Id);
        var mergedAt = Now.AddHours(-2);

        await Sweeper(new SourceHost(found: new() { [branch] = Open with { MergedAt = mergedAt } }))
            .SweepAsync(Now, default);

        var watching = await new PostgresCveFixVerification(pg.ConnectionString).ListWatchingAsync();
        Assert.Equal(mergedAt, Assert.Single(watching, w => w.RunId == bump.Id).MergedAt);
    }

    [Fact]
    public async Task A_stopped_bump_the_host_cannot_be_asked_about_stays_running()
    {
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        await new GithubInstallationRepository(pg.ConnectionString).LinkAsync(InstallationIdFor(orgId), orgId);
        await Quota().TryConsumeAsync(orgId, Limit, Now);
        var repo = await new RepoLinkRepository(pg.ConnectionString).LinkAsync(projectId, "acme/api", "main");
        var cves = new PostgresCveFixStore(pg.ConnectionString);
        var bump = Bump(repo.Id);
        await cves.InsertAsync(bump);

        await Sweeper(new SourceHost(found: [], fails: true)).SweepAsync(Now, default);

        Assert.Equal(FixStatus.Running, (await cves.ListByRepoAsync(repo.Id)).Single(r => r.Id == bump.Id).Status);
        Assert.Equal(1, await Quota().GetUsedAsync(orgId, Now));
    }

    [Fact]
    public async Task A_run_that_concluded_before_the_record_keeps_its_conclusion()
    {
        // The lookup runs outside any transaction, so the run can fail, and be refunded, by its own write or
        // another conductor's sweep before the record lands. Overwriting that would keep a refund for a run
        // recorded as a success.
        var (_, issueId) = await SeedIssueAsync(withInstallation: true);
        var fixId = await InsertRunningFixAsync(issueId);
        var sweep = new PostgresStaleRunSweep(pg.ConnectionString);
        var listed = (await sweep.ListRunningAsync(Now.AddHours(-1))).Single(r => r.Id == fixId);
        await sweep.FailStaleAsync(Now.AddHours(-1), Now.AddHours(-1), Now, [fixId]);

        Assert.False(await sweep.TryRecordFoundAsync(listed, "condux/fix-x", Open with { MergedAt = Now }, Now));

        var run = await Fixes().GetAsync(fixId);
        Assert.Equal(FixStatus.Failed, run!.Status);
        Assert.Equal("", run.PrUrl);
    }

    [Fact]
    public async Task A_run_still_inside_the_cutoff_is_not_looked_up()
    {
        var (_, issueId) = await SeedIssueAsync(withInstallation: true);
        var sweeper = Sweeper(new SourceHost(found: []));
        await InsertRunningFixAsync(issueId, at: Now - sweeper.StaleAfter + TimeSpan.FromMinutes(1));
        var host = new SourceHost(found: []);

        await Sweeper(host).SweepAsync(Now, default);

        Assert.Empty(host.Asked);
    }

    // GitHub as the sweep sees it: a pull request URL per branch that has one, or every lookup failing.
    private sealed class SourceHost(Dictionary<string, FoundPullRequest?> found, bool fails = false)
        : ISourceHostClient, ISourceHostTokens
    {
        public List<(string Repo, string Branch)> Asked { get; } = [];

        public Task<string> GetAsync(long installationId, CancellationToken ct = default) =>
            Task.FromResult($"token-{installationId}");

        public Task<FoundPullRequest?> FindPullRequestAsync(
            string token, string repoFullName, string headBranch, CancellationToken ct = default)
        {
            Asked.Add((repoFullName, headBranch));
            return fails
                ? throw new HttpRequestException("GitHub unreachable")
                : Task.FromResult(found.GetValueOrDefault(headBranch));
        }

        public Task<RepoFile?> GetFileAsync(string token, string repoFullName, string path, string gitRef,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<string> GetBranchHeadShaAsync(string token, string repoFullName, string branch,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task CreateBranchAsync(string token, string repoFullName, string branch, string fromSha,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task PutFileAsync(string token, string repoFullName, string path, string branch, string message,
            string content, string? existingSha, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListBranchesAsync(string token, string repoFullName,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListInstallationRepositoriesAsync(string token,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListTreeAsync(string token, string repoFullName, string gitRef,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<SuspectCommit?> GetLatestCommitTouchingAsync(string token, string repoFullName, string path,
            string sha, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<string> OpenDraftPullRequestAsync(string token, string repoFullName, string headBranch,
            string baseBranch, string title, string body, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // The lookup can be given its own store, so a test can break it alone.
    private StaleRunSweeper Sweeper(SourceHost host, string? lookupConnection = null)
    {
        var sweep = new PostgresStaleRunSweep(pg.ConnectionString);
        var notifier = new ProjectEventNotifier(pg.ConnectionString);
        return new StaleRunSweeper(
            NullLogger<StaleRunSweeper>.Instance, sweep, Fixes(), notifier,
            new Condux.Telemetry.ConduxSelfReporter(null), ManagedAgentOptions.Default,
            new UnrecordedPullRequests(
                NullLogger<UnrecordedPullRequests>.Instance,
                new PostgresStaleRunSweep(lookupConnection ?? pg.ConnectionString), Fixes(), notifier, host, host));
    }

    private PostgresAiFixQuota Quota() => new(pg.ConnectionString);

    private PostgresFixStore Fixes() => new(pg.ConnectionString);

    // Unique per org, since an installation belongs to one org and the fixture database is shared.
    private static long InstallationIdFor(long orgId) => 900_000_000 + orgId;

    private async Task<(long OrgId, long IssueId)> SeedIssueAsync(bool withInstallation)
    {
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        if (withInstallation)
        {
            Assert.True(await new GithubInstallationRepository(pg.ConnectionString)
                .LinkAsync(InstallationIdFor(orgId), orgId));
        }
        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping($"fp-{Guid.NewGuid():N}", "TypeError: boom", "run"), Level.Error, Now);
        return (orgId, issue.Id);
    }

    // Running and untouched for a day unless a test says otherwise, so past the running cutoff but not the
    // pending one.
    private async Task<Guid> InsertRunningFixAsync(long issueId, DateTimeOffset? at = null)
    {
        var id = Guid.CreateVersion7();
        var stamp = at ?? Now.AddDays(-1);
        Assert.True(await Fixes().InsertAsync(new FixSuggestion(
            id, issueId, "acme/api", FixStatus.Running, "anthropic", "model", "", "", "", stamp, stamp)
        { ReservedAt = Now }));
        return id;
    }

    private static CveFixRun Bump(Guid repoLinkId) => new(
        Guid.CreateVersion7(), repoLinkId, "GHSA-jf85-cpcp-j695", "CVE-2021-23337", "lodash", "npm",
        "< 4.17.21", "4.17.21", "https://example.invalid/advisory", FixStatus.Running, "anthropic", "model",
        Branch: "", PrUrl: "", Summary: "", Actor: "42", Now.AddDays(-1), Now.AddDays(-1))
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
