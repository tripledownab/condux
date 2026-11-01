using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The migration that allows one fix run in progress per issue must apply over a database that already
/// holds two, since nothing prevented that before it. A unique index cannot be built over duplicates, so
/// a migration that only created it would fail the deploy that carries it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FixRunInProgressMigrationTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string Migration = "0059_fix_run_in_progress_per_issue.sql";

    [Fact]
    public async Task Duplicates_left_from_before_are_cancelled_keeping_the_running_one_and_the_index_is_built()
    {
        // The running run may have a worker on it, so it is the one kept, though the pending one is newer.
        var issue = await SeedIssueAsync();
        var otherIssue = await SeedIssueAsync();
        await ExecuteAsync("DROP INDEX fix_suggestions_active_uq;");
        var running = await InsertAsync(issue, FixStatus.Running, DateTimeOffset.UtcNow.AddHours(-2));
        var pending = await InsertAsync(issue, FixStatus.Pending, DateTimeOffset.UtcNow.AddHours(-1));
        var done = await InsertAsync(issue, FixStatus.Succeeded, DateTimeOffset.UtcNow.AddHours(-3));
        // With none running, the newest pending one is kept.
        var olderPending = await InsertAsync(otherIssue, FixStatus.Pending, DateTimeOffset.UtcNow.AddHours(-2));
        var newerPending = await InsertAsync(otherIssue, FixStatus.Pending, DateTimeOffset.UtcNow.AddHours(-1));

        await ApplyMigrationAsync();
        // A file runs again whenever the migration files change other than by new ones sorting last, so a
        // second pass must change nothing.
        await ApplyMigrationAsync();

        var fixes = new PostgresFixStore(pg.ConnectionString);
        Assert.Equal(FixStatus.Running, (await fixes.GetAsync(running))!.Status);
        var cancelled = (await fixes.GetAsync(pending))!;
        Assert.Equal(FixStatus.Cancelled, cancelled.Status);
        // Not marked interrupted: that reads as a run the sweep refunded, and a late success would be
        // charged again.
        Assert.Null(cancelled.FailureReason);
        Assert.Equal(FixStatus.Succeeded, (await fixes.GetAsync(done))!.Status);
        Assert.Equal(FixStatus.Cancelled, (await fixes.GetAsync(olderPending))!.Status);
        Assert.Equal(FixStatus.Pending, (await fixes.GetAsync(newerPending))!.Status);
        Assert.False(await fixes.TryQueueAsync(new FixJob(issue, "acme/api", "main", "42", "model")
            .ToPendingRun(provider: "", DateTimeOffset.UtcNow)));
    }

    private async Task<long> SeedIssueAsync()
    {
        var (_, projectId) = await ProjectSeed.CreateOrgAndProjectAsync(pg.ConnectionString);
        var issue = await new IssueRepository(pg.ConnectionString).UpsertAsync(
            projectId, new Grouping($"fp-{Guid.NewGuid():N}", "TypeError: boom", "run"), Level.Error,
            DateTimeOffset.UtcNow);
        return issue.Id;
    }

    private async Task<Guid> InsertAsync(long issueId, FixStatus status, DateTimeOffset at)
    {
        var id = Guid.CreateVersion7();
        Assert.True(await new PostgresFixStore(pg.ConnectionString).InsertAsync(
            new FixSuggestion(id, issueId, "acme/api", status, "anthropic", "model", "", "", "", at, at)));
        return id;
    }

    private async Task ApplyMigrationAsync() =>
        await ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "migrations", Migration)));

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
