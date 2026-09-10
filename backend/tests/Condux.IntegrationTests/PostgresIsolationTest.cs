using Condux.IntegrationTests.Fixtures;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Every test class used to get its own Postgres container, so nothing had to assert that one class
/// cannot see another's rows. They now share a server and are separated by a database instead, and this
/// is what holds that mechanism honest: give the template a fixed name, or hand two classes the same
/// database, and this fails where the rest of the suite would mostly carry on passing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresIsolationTest
{
    [Fact]
    public async Task Two_databases_from_the_same_server_are_migrated_and_cannot_see_each_other()
    {
        var first = await PostgresServer.CreateDatabaseAsync();
        var second = await PostgresServer.CreateDatabaseAsync();

        Assert.NotEqual(DatabaseOf(first), DatabaseOf(second));

        // Migrated: a table only the migrations create, and empty, so the copy carries the schema
        // without carrying another class's rows.
        Assert.Equal(0, await ScalarAsync(first, "SELECT count(*) FROM orgs"));
        Assert.Equal(0, await ScalarAsync(second, "SELECT count(*) FROM orgs"));

        await ExecuteAsync(first, "INSERT INTO orgs (slug, name) VALUES ('iso-a', 'Isolation A')");

        Assert.Equal(1, await ScalarAsync(first, "SELECT count(*) FROM orgs"));
        Assert.Equal(0, await ScalarAsync(second, "SELECT count(*) FROM orgs"));
    }

    private static string DatabaseOf(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString).Database!;

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
