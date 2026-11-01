using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The weekly digest's new and regressed counts read Condux's own stamps, created_at and
/// reopened_at, and fall back to the old rule for a row written before migration 0065 added them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WeeklyIssueCountsTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddDays(7);
    private static readonly DateTimeOffset Before = Start.AddDays(-10);
    private static readonly DateTimeOffset Inside = Start.AddDays(2);
    private static readonly DateTimeOffset After = End.AddDays(1);

    [Fact]
    public async Task Counts_by_condux_stamps_and_reads_older_rows_by_the_old_rule()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);

        // Written before 0065, so both stamps are NULL.
        await InsertAsync(projectId, firstSeen: Inside, activated: Inside, created: null, reopened: null); // new
        await InsertAsync(projectId, firstSeen: Before, activated: Inside, created: null, reopened: null); // regressed
        await InsertAsync(projectId, firstSeen: Inside, activated: Before, created: null, reopened: null); // new only

        // Written since. A late first event: new, and not a regression however far back it claims.
        await InsertAsync(projectId, firstSeen: Before, activated: Inside, created: Inside, reopened: null);
        await InsertAsync(projectId, firstSeen: Before, activated: Inside, created: Before, reopened: Inside); // regressed
        await InsertAsync(projectId, firstSeen: Before, activated: Before, created: Before, reopened: Before);
        await InsertAsync(projectId, firstSeen: Before, activated: After, created: Before, reopened: After);
        await InsertAsync(projectId, firstSeen: Before, activated: After, created: After, reopened: null);

        var counts = await new IssueRepository(pg.ConnectionString).WeeklyIssueCountsAsync([projectId], Start, End);

        Assert.Equal(3, counts.New);
        Assert.Equal(2, counts.Regressed);
    }

    [Fact]
    public async Task Replaying_the_migration_changes_nothing()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        await InsertAsync(projectId, firstSeen: Before, activated: Inside, created: Inside, reopened: Inside);
        var migration = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "migrations", "0065_issue_processing_times.sql"));

        await ExecuteAsync(migration);

        var counts = await new IssueRepository(pg.ConnectionString).WeeklyIssueCountsAsync([projectId], Start, End);
        Assert.Equal(1, counts.New);
        Assert.Equal(0, counts.Regressed);
    }

    private async Task InsertAsync(
        long projectId, DateTimeOffset firstSeen, DateTimeOffset activated, DateTimeOffset? created,
        DateTimeOffset? reopened)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO issues (project_id, fingerprint, title, culprit, level, first_seen, last_seen, event_count,
                                public_id, activated_at, created_at, reopened_at)
            VALUES (@p, @fp, 'Crash', 'run', 4, @first, @first, 1, gen_random_uuid(), @activated, @created, @reopened)
            """, conn);
        cmd.Parameters.AddWithValue("p", projectId);
        cmd.Parameters.AddWithValue("fp", $"fp-{Guid.NewGuid():N}");
        cmd.Parameters.AddWithValue("first", firstSeen);
        cmd.Parameters.AddWithValue("activated", activated);
        cmd.Parameters.AddWithValue("created", (object?)created ?? DBNull.Value);
        cmd.Parameters.AddWithValue("reopened", (object?)reopened ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
