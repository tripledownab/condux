using Npgsql;
using Xunit;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// A private, already-migrated Postgres database for one test class.
///
/// The isolation a test class gets is unchanged: CREATE DATABASE ... TEMPLATE copies the template block
/// by block through the write-ahead log, which is what the WAL_LOG strategy does and what Postgres has
/// defaulted to since 15, so a class still gets its own database and still cannot see what another class
/// wrote. That strategy is also the documented right one here, being the efficient choice while the
/// template is small. What changed is the cost, not the isolation.
/// <see cref="PostgresServer"/> starts one server and migrates one template for the whole run, so a class
/// no longer starts a container, and a test no longer replays the migrations before it can begin.
///
/// Removing that replay also removed a side effect nobody wanted. Four migrations carry a backfill, and
/// two of them are not restricted to rows that predate the column: 0029 sets onboarded_at on every user
/// who belongs to an org and lacks one, 0037 sets activated_at on every issue that lacks one. Replaying
/// them between two tests of the same class therefore reached rows the first test had just written.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private string? _connectionString;

    public string ConnectionString => _connectionString ?? throw new InvalidOperationException(
        "The database is created in InitializeAsync, so the connection string cannot be read from a field "
        + "initialiser or a constructor. Read it inside a test method, or from a member evaluated lazily.");

    public async Task InitializeAsync() => _connectionString = await PostgresServer.CreateDatabaseAsync();

    // The database is not dropped. It goes when the container does, and dropping it would need a FORCE
    // (a host under test may still hold a connection), which can only ever turn a passing run red. A
    // freshly migrated one measures 9MB, inside a container that does not outlive the run.
    //
    // The connection pool IS released, because a pool is per connection string and so is exactly this
    // class's. Npgsql keeps a closed connection open in its pool for longer than a class runs, and every
    // class now draws on one server's max_connections instead of on a container of its own. Measured on
    // the full suite, the run peaks at 117 connections with this call and 366 without it. It mattered
    // far less before the collections ran in parallel, so do not read an old measurement as the reason
    // it is here.
    public Task DisposeAsync()
    {
        if (_connectionString is null)
        {
            return Task.CompletedTask;
        }

        // Hosts first, then the pool. Stopping a host returns its connections to the pool, so clearing
        // the pool before that would leave behind exactly the ones this is here to release.
        ControlPlaneApp.DisposeHostsFor(_connectionString);
        using var conn = new NpgsqlConnection(_connectionString);
        NpgsqlConnection.ClearPool(conn);
        return Task.CompletedTask;
    }
}
