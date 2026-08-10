using Condux.IntegrationTests.Fixtures;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>Sanity check that the Testcontainers fixture wiring works end to end.</summary>
[Trait("Category", "Integration")]
public class PostgresSmokeTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task CanConnectAndQuery()
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT 1", conn);
        Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
    }
}
