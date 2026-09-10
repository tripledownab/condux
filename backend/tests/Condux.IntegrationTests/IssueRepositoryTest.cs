using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.Core.Issues;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

[Trait("Category", "Integration")]
public class IssueRepositoryTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Upsert_GroupsByFingerprint_IncrementsCount()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var repo = new IssueRepository(pg.ConnectionString);
        var grouping = new Grouping("fp-1", "ValueError: boom", "run");

        var r1 = await repo.UpsertAsync(projectId, grouping, Level.Error, DateTimeOffset.UtcNow);
        var r2 = await repo.UpsertAsync(projectId, grouping, Level.Error, DateTimeOffset.UtcNow);

        Assert.Equal(r1.Id, r2.Id); // same fingerprint → same issue
        Assert.Equal(1L, r1.Occurrence); // upsert returns the new 1-based occurrence count
        Assert.Equal(2L, r2.Occurrence);
        Assert.Equal(2L, await EventCountAsync(r1.Id));
    }

    [Fact]
    public async Task List_ReturnsProjectIssues()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var repo = new IssueRepository(pg.ConnectionString);
        await repo.UpsertAsync(projectId, new Grouping("fp-L", "Err: x", "run"), Level.Error, DateTimeOffset.UtcNow);

        var (issues, hasMore) = await repo.ListPageAsync(
            projectId, IssueQuery.Parse(null), currentUserId: 0, "lastSeen", limit: 50, offset: 0);

        Assert.False(hasMore);
        var issue = Assert.Single(issues);
        Assert.Equal("fp-L", issue.Fingerprint);
        Assert.Equal("Err: x", issue.Title);
        Assert.Equal(1L, issue.EventCount);
    }

    [Fact]
    public async Task Upsert_RecordsTheFirstReleaseSeen_AndKeepsIt()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var repo = new IssueRepository(pg.ConnectionString);
        var grouping = new Grouping("fp-rel", "Err: rel", "run");

        // First occurrence carries no release, second is "1.0.0", third is "2.0.0".
        var r1 = await repo.UpsertAsync(projectId, grouping, Level.Error, DateTimeOffset.UtcNow, release: null);
        await repo.UpsertAsync(projectId, grouping, Level.Error, DateTimeOffset.UtcNow, release: "1.0.0");
        await repo.UpsertAsync(projectId, grouping, Level.Error, DateTimeOffset.UtcNow, release: "2.0.0");

        // It fills once a release appears and then keeps the first one (1.0.0, not 2.0.0).
        var issue = await repo.GetByPublicIdAsync(projectId, r1.PublicId);
        Assert.Equal("1.0.0", issue!.Value.Summary.FirstRelease);
    }

    private async Task<long> EventCountAsync(long id)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT event_count FROM issues WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }
}
