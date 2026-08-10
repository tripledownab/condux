using Testcontainers.PostgreSql;
using Xunit;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>Ephemeral Postgres container for integration tests (one per test class).</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } =
        new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();

    public string ConnectionString => Container.GetConnectionString();

    public Task InitializeAsync() => Container.StartAsync();

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}
