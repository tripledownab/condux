using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Regression detection in the issue upsert (#55): a new event carries the issue's public id and, when
/// it lands on a previously resolved issue, reopens it and reports <c>Reopened</c> so the consumer can
/// fire a regression alert.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IssueRegressionTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Upsert_NewIssue_IsNotReopened_AndCarriesPublicId()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var repo = new IssueRepository(pg.ConnectionString);

        var result = await repo.UpsertAsync(
            projectId, new Grouping("fp-reg-new", "Err: x", "run"), Level.Error, DateTimeOffset.UtcNow);

        Assert.Equal(1L, result.Occurrence);
        Assert.False(result.Reopened);
        Assert.NotEqual(Guid.Empty, result.PublicId);
    }

    [Fact]
    public async Task Upsert_OnResolvedIssue_ReopensIt_AndReportsRegression()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var repo = new IssueRepository(pg.ConnectionString);
        var grouping = new Grouping("fp-reg", "TypeError: boom", "run");

        var first = await repo.UpsertAsync(projectId, grouping, Level.Error, DateTimeOffset.UtcNow);
        await SetStatusAsync(first.Id, resolved: 2);

        var reopened = await repo.UpsertAsync(projectId, grouping, Level.Error, DateTimeOffset.UtcNow);

        Assert.True(reopened.Reopened);
        Assert.Equal(first.PublicId, reopened.PublicId); // public id preserved across the reopen
        Assert.Equal(1, await StatusAsync(first.Id)); // resolved (2) flipped back to unresolved (1)
    }

    private async Task SetStatusAsync(long id, int resolved)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("UPDATE issues SET status = @s WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("s", (short)resolved);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> StatusAsync(long id)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT status FROM issues WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
