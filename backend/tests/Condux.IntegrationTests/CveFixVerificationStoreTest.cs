using Condux.Core.CveFix;
using Condux.Core.FixEngine;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The CVE bump verification's persistence (ADR-0051) against real Postgres: every path that writes a run
/// stores the advisory's vulnerable ranges, a merged PR marks the bump by repository and branch through its
/// link, the watcher lists it with what its verdict needs, and a conclusion lands once with its evidence.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CveFixVerificationStoreTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] Ranges = ["< 4.17.21", ">= 5.0.0, < 5.0.3"];

    [Fact]
    public async Task Every_path_that_writes_a_run_stores_its_ranges()
    {
        var (_, repoId, _) = await SeedRepoAsync();
        var queued = Bump(repoId) with { JobId = Guid.CreateVersion7() };
        var cancelled = Bump(repoId) with { JobId = Guid.CreateVersion7(), GhsaId = "GHSA-cancel" };
        var inserted = Bump(repoId) with { JobId = Guid.CreateVersion7(), GhsaId = "GHSA-insert" };
        var leased = Bump(repoId) with { GhsaId = "GHSA-runner" };

        Assert.True(await Store().TryQueueAsync(queued));
        Assert.True(await Store().TryCancelQueuedAsync(cancelled));
        Assert.True(await Store().InsertAsync(inserted));
        Assert.True(await new PostgresJobLeaseStore(pg.ConnectionString).EnqueueCveAsync(
            leased, new RunnerJobContext("main", "Bump lodash.", ["package.json"])));

        foreach (var run in new[] { queued, cancelled, inserted, leased })
        {
            Assert.Equal(Ranges, await ReadRangesAsync(run.Id));
        }
    }

    /// <summary>Cancelling a run that was already written changes its status and keeps its ranges.</summary>
    [Fact]
    public async Task Cancelling_a_queued_run_keeps_its_ranges()
    {
        var (_, repoId, _) = await SeedRepoAsync();
        var run = Bump(repoId) with { JobId = Guid.CreateVersion7() };
        Assert.True(await Store().TryQueueAsync(run));

        Assert.True(await Store().TryCancelQueuedAsync(run with { VulnerableRanges = null }));

        Assert.Equal(Ranges, await ReadRangesAsync(run.Id));
    }

    [Fact]
    public async Task A_run_with_no_ranges_stores_none()
    {
        var (_, repoId, _) = await SeedRepoAsync();
        var run = Bump(repoId) with { JobId = Guid.CreateVersion7(), VulnerableRanges = null };

        Assert.True(await Store().TryQueueAsync(run));

        Assert.Null(await ReadRangesAsync(run.Id));
    }

    [Fact]
    public async Task A_merged_bump_is_watched_and_concludes_once()
    {
        var (_, repoId, projectId) = await SeedRepoAsync();
        var run = await SucceedAsync(Bump(repoId) with { JobId = Guid.CreateVersion7() }, "condux/cve-lodash");
        var verification = new PostgresCveFixVerification(pg.ConnectionString);

        // Another branch, or the right branch on another repository, marks nothing.
        Assert.Empty(await verification.MarkMergedAsync("acme/api", "condux/other", Now));
        Assert.Empty(await verification.MarkMergedAsync("acme/web", "condux/cve-lodash", Now));

        Assert.Equal([(run.Id, projectId)], await verification.MarkMergedAsync("acme/api", "condux/cve-lodash", Now));
        // A second delivery of the webhook finds it already merged.
        Assert.Empty(await verification.MarkMergedAsync("acme/api", "condux/cve-lodash", Now.AddMinutes(1)));

        var watched = Assert.Single(await verification.ListWatchingAsync(), w => w.RunId == run.Id);
        Assert.Equal(projectId, watched.ProjectId);
        Assert.Equal(("lodash", "npm", "4.17.21"), (watched.Package, watched.Ecosystem, watched.ToVersion));
        Assert.Equal(Ranges, watched.VulnerableRanges);
        Assert.Equal(Now, watched.MergedAt);

        var evidence = new CveFixEvidence(
            Now.AddDays(3), Now, Now, "4.17.21", Ranges, Complete: true, Reason: null,
            [new CveFixEvidenceSighting("4.17.21", "production", "1.4.2", Now.AddDays(1), SightingClass.Fixed, InWindow: true)]);
        Assert.True(await verification.SetVerifyStatusAsync(run.Id, VerifyStatus.Held, evidence.ToJson(), Now.AddDays(3)));
        Assert.False(await verification.SetVerifyStatusAsync(run.Id, VerifyStatus.DidNotHold, "{}", Now.AddDays(4)));

        Assert.DoesNotContain(await verification.ListWatchingAsync(), w => w.RunId == run.Id);
        Assert.Equal((short)VerifyStatus.Held, (await ReadConclusionAsync(run.Id)).Status);

        // The run as the API returns it carries the verdict and the evidence it rested on.
        var read = Assert.Single(await Store().ListByRepoAsync(repoId), r => r.Id == run.Id);
        Assert.Equal((VerifyStatus.Held, Now, Now.AddDays(3)), (read.VerifyStatus, read.MergedAt, read.VerifiedAt));
        Assert.Equal("4.17.21", read.VerifyEvidence!.Target);
        Assert.Equal(Ranges, read.VerifyEvidence.Ranges);
        Assert.Equal(SightingClass.Fixed, Assert.Single(read.VerifyEvidence.Sightings).Class);
    }

    [Fact]
    public async Task A_bump_that_did_not_succeed_is_not_marked_merged()
    {
        var (_, repoId, _) = await SeedRepoAsync();
        var run = Bump(repoId) with { JobId = Guid.CreateVersion7(), Branch = "condux/cve-pending" };
        Assert.True(await Store().InsertAsync(run));

        Assert.Empty(await new PostgresCveFixVerification(pg.ConnectionString)
            .MarkMergedAsync("acme/api", "condux/cve-pending", Now));
    }

    [Fact]
    public async Task A_watched_bump_requested_before_ranges_were_stored_lists_none()
    {
        var (_, repoId, _) = await SeedRepoAsync();
        var run = await SucceedAsync(
            Bump(repoId) with { JobId = Guid.CreateVersion7(), VulnerableRanges = null }, "condux/cve-old");
        var verification = new PostgresCveFixVerification(pg.ConnectionString);
        await verification.MarkMergedAsync("acme/api", "condux/cve-old", Now);

        Assert.Empty(Assert.Single(await verification.ListWatchingAsync(), w => w.RunId == run.Id).VulnerableRanges);
    }

    private PostgresCveFixStore Store() => new(pg.ConnectionString);

    private async Task<CveFixRun> SucceedAsync(CveFixRun run, string branch)
    {
        Assert.True(await Store().InsertAsync(run));
        var succeeded = run with { Status = FixStatus.Succeeded, Branch = branch, PrUrl = "https://example.invalid/p/1" };
        await Store().RecordSuccessAsync(succeeded);
        return succeeded;
    }

    private async Task<(long OrgId, Guid RepoId, long ProjectId)> SeedRepoAsync()
    {
        var (orgId, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var repo = await new RepoLinkRepository(pg.ConnectionString).LinkAsync(projectId, "acme/api", "main");
        return (orgId, repo.Id, projectId);
    }

    private async Task<string[]?> ReadRangesAsync(Guid runId)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT vulnerable_ranges FROM cve_fix_runs WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", runId);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : (string[])value!;
    }

    private async Task<(short Status, string Evidence)> ReadConclusionAsync(Guid runId)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT verify_status, verify_evidence::text FROM cve_fix_runs WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", runId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt16(0), reader.GetString(1));
    }

    private static CveFixRun Bump(Guid repoLinkId) => new(
        Guid.CreateVersion7(), repoLinkId, "GHSA-jf85-cpcp-j695", "CVE-2021-23337", "lodash", "npm",
        "< 4.17.21", "4.17.21", "https://example.invalid/advisory", FixStatus.Running, "anthropic", "model",
        Branch: "", PrUrl: "", Summary: "", Actor: "42", Now, Now)
    { ReservedAt = Now, VulnerableRanges = Ranges };
}
