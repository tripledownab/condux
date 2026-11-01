using Condux.Core.CveFix;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The text a fix run records is written by a model or a runner, and Postgres <c>text</c> refuses U+0000
/// with 22021, so a NUL in it failed the write: a runner's report was retried until its lease lapsed, and a
/// hosted run's success left its draft PR recorded nowhere. Each write stores the character as U+FFFD, the
/// run as written. Each statement adds its own parameters, so each is exercised here: the runner's
/// report, both inserts and the bump's outcome. The issue fix's outcome is ConductorTest's.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunTextStorageTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string WithNul = "a\0b";
    private const string Stored = "a�b";

    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_runner_report_stores_each_text_it_carries()
    {
        var (orgId, _, issueId) = await SeedIssueAsync();
        var leases = new PostgresJobLeaseStore(pg.ConnectionString);
        await leases.EnqueueAsync(
            Guid.CreateVersion7(), issueId, "acme/api", new RunnerJobContext("main", "Fix it.", []), Now);
        var job = await leases.TryClaimAsync(orgId, Now);

        Assert.NotNull(await leases.TryReportAsync(
            job!.FixId, job.LeaseId, FixStatus.Succeeded, $"branch {WithNul}", $"url {WithNul}",
            $"summary {WithNul}", $"model {WithNul}", 1, 1, Now.AddMinutes(1)));

        var stored = await new PostgresFixStore(pg.ConnectionString).GetAsync(job.FixId);
        Assert.Equal($"branch {Stored}", stored!.Branch);
        Assert.Equal($"url {Stored}", stored.PrUrl);
        Assert.Equal($"summary {Stored}", stored.Summary);
        Assert.Equal($"model {Stored}", stored.Model);
    }

    [Fact]
    public async Task An_issue_fix_stores_a_summary_holding_a_nul_on_insert()
    {
        var (_, _, issueId) = await SeedIssueAsync();
        var store = new PostgresFixStore(pg.ConnectionString);
        var run = new FixSuggestion(
            Guid.CreateVersion7(), issueId, "acme/api", FixStatus.Running, "fake", ModelDefaults.Fix,
            "", "", WithNul, Now, Now);

        Assert.True(await store.InsertAsync(run));

        Assert.Equal(Stored, (await store.GetAsync(run.Id))!.Summary);
    }

    [Fact]
    public async Task A_bump_stores_a_summary_holding_a_nul_on_insert_and_on_its_outcome()
    {
        var store = new PostgresCveFixStore(pg.ConnectionString);
        var repoId = await SeedRepoAsync();
        var run = Bump(repoId) with { Summary = WithNul };

        Assert.True(await store.InsertAsync(run));
        Assert.Equal(Stored, Assert.Single(await store.ListByRepoAsync(repoId)).Summary);

        await store.RecordSuccessAsync(run with
        {
            Status = FixStatus.Succeeded,
            PrUrl = "https://example.invalid/p/1",
            Summary = $"bumped {WithNul}",
        });
        Assert.Equal($"bumped {Stored}", Assert.Single(await store.ListByRepoAsync(repoId)).Summary);
    }

    private async Task<(long OrgId, long ProjectId, long IssueId)> SeedIssueAsync()
    {
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping($"fp-{Guid.NewGuid():N}", "ValueError: boom", "run"),
            Level.Error, DateTimeOffset.UtcNow);
        return (orgId, projectId, issue.Id);
    }

    private async Task<Guid> SeedRepoAsync()
    {
        var (_, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        return (await new RepoLinkRepository(pg.ConnectionString).LinkAsync(projectId, "acme/api", "main")).Id;
    }

    private static CveFixRun Bump(Guid repoLinkId) => new(
        Guid.CreateVersion7(), repoLinkId, "GHSA-jf85-cpcp-j695", "CVE-2021-23337", "lodash", "npm",
        "< 4.17.21", "4.17.21", "https://example.invalid/advisory", FixStatus.Running, "anthropic", "model",
        Branch: "", PrUrl: "", Summary: "", Actor: "42", Now, Now)
    { ReservedAt = Now };
}
