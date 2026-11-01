using Condux.IntegrationTests.Fixtures;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The migration that moves level 0 out of stored alert rules and issues. A rule that listed 0 to catch
/// level-less crashes now gets Error, the level those crashes carry, and keeps its other levels. A rule left
/// with no levels would never fire, and one still holding 0 could not be saved by the dashboard, since the
/// API refuses 0. An issue stored at 0 becomes Error too, since its resolve and assign alerts fire at it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AlertRuleLevelsMigrationTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string Migration = "0063_alert_rule_levels_no_unspecified.sql";

    [Fact]
    public async Task Level_zero_becomes_error_without_duplicates_and_other_rules_are_untouched()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var onlyZero = await InsertRuleAsync(projectId, [0]);
        var zeroAndError = await InsertRuleAsync(projectId, [0, 4, 5]);
        var zeroAndDebug = await InsertRuleAsync(projectId, [1, 0]);
        var zeroLast = await InsertRuleAsync(projectId, [5, 0]);
        var untouched = await InsertRuleAsync(projectId, [5, 2]);

        await ApplyMigrationAsync();
        // A file runs again whenever the migration files change other than by new ones sorting last, so a
        // second pass must change nothing.
        await ApplyMigrationAsync();

        Assert.Equal([4], await LevelsAsync(onlyZero));
        Assert.Equal([4, 5], await LevelsAsync(zeroAndError));
        Assert.Equal([1, 4], await LevelsAsync(zeroAndDebug));
        Assert.Equal([4, 5], await LevelsAsync(zeroLast));
        Assert.Equal([5, 2], await LevelsAsync(untouched));
    }

    [Fact]
    public async Task Issues_stored_at_level_zero_become_errors_and_others_keep_their_level()
    {
        // A resolve or assign alert fires at the issue's stored level, so an issue left at 0 would match no
        // rule once the rules no longer list 0.
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var issues = new Condux.Storage.Postgres.IssueRepository(pg.ConnectionString);
        var atZero = await issues.UpsertAsync(projectId,
            new Condux.Core.Grouping.Grouping($"fp-{Guid.NewGuid():N}", "Crash", "run"),
            Condux.Core.Events.Level.Unspecified, DateTimeOffset.UtcNow);
        var atWarning = await issues.UpsertAsync(projectId,
            new Condux.Core.Grouping.Grouping($"fp-{Guid.NewGuid():N}", "Slow", "run"),
            Condux.Core.Events.Level.Warning, DateTimeOffset.UtcNow);

        await ApplyMigrationAsync();
        await ApplyMigrationAsync();

        Assert.Equal(4, await IssueLevelAsync(atZero.Id));
        Assert.Equal(3, await IssueLevelAsync(atWarning.Id));
    }

    private async Task<short> IssueLevelAsync(long issueId)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT level FROM issues WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", issueId);
        return (short)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<Guid> InsertRuleAsync(long projectId, short[] levels)
    {
        var id = Guid.NewGuid();
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO alert_rules (id, project_id, name, levels) VALUES (@id, @p, 'rule', @levels)", conn);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("p", projectId);
        cmd.Parameters.AddWithValue("levels", levels);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private async Task<short[]> LevelsAsync(Guid ruleId)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT levels FROM alert_rules WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", ruleId);
        return (short[])(await cmd.ExecuteScalarAsync())!;
    }

    private async Task ApplyMigrationAsync()
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "migrations", Migration)), conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
