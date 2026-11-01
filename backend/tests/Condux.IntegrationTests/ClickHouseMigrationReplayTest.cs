using Condux.IntegrationTests.Fixtures;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Every deploy replays every ClickHouse migration against the live database (compose's clickhouse-init,
/// the Helm migrations job), and the consumer can still be writing: compose leaves it running when its
/// image did not change, and Helm runs the job before the upgrade. So a file that creates what a later one
/// drops brings it back for the moments between the two: 0001 used to recreate the view 0003 drops, and
/// the view counted the events stored in that window a second time. Replaying the files in order over a
/// migrated database must never leave an object the migrated schema does not have. It compares each
/// table's name and engine in condux after each file, so a changed column, TTL or view definition, or an
/// object outside condux, is beyond it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ClickHouseMigrationReplayTest(ClickHouseFixture ch) : IClassFixture<ClickHouseFixture>
{
    [Fact]
    public async Task Replaying_the_migrations_never_brings_back_what_a_later_one_dropped()
    {
        var migrated = await ObjectsAsync();
        Assert.Contains("events MergeTree", migrated);

        foreach (var file in ClickHouseFixture.MigrationFiles())
        {
            await ch.ApplyMigrationAsync(file);

            var extra = (await ObjectsAsync()).Except(migrated).ToList();
            Assert.True(extra.Count == 0,
                $"replaying {Path.GetFileName(file)} left {string.Join(", ", extra)}, which the migrated schema lacks");
        }

        Assert.Equal(migrated, await ObjectsAsync());
    }

    // Each table and view in condux by name and engine, so a view standing in for a table counts as new.
    private async Task<HashSet<string>> ObjectsAsync() =>
        (await ch.QueryAsync(
            "SELECT name || ' ' || engine FROM system.tables WHERE database = 'condux' FORMAT TabSeparated"))
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .ToHashSet();
}
