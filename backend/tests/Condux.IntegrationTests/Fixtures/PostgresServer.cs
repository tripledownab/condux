using Npgsql;
using Testcontainers.PostgreSql;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>
/// The one Postgres server the integration suite runs against, and the migrated template that every
/// test class is copied from.
///
/// It exists because none of that setup differed between classes and all of it was repeated. The suite
/// started a Postgres container per test class and replayed every migration once per TEST: 69 containers
/// and 244 replays to run 313 tests. Both are done once here instead, and a class takes a copy.
///
/// The copy is what let the suite go parallel, which is where most of the time actually went. Running
/// 69 containers at once was never an option; running 69 databases on one server is. Measured locally
/// across several runs: 523s to 550s before, 283s to 314s after. It has never been measured on CI.
///
/// Applying the migrations is private to this class on purpose. It was a shared helper the test classes
/// called themselves, which is how the replay came to sit in 244 methods, and a call that cannot be
/// written cannot come back.
///
/// Nothing disposes the container. It belongs to the whole run, and the Testcontainers resource reaper
/// (the testcontainers/ryuk sidecar it starts by itself) removes it when the test process ends.
/// </summary>
internal static class PostgresServer
{
    /// <summary>The migrated database every test database is copied from. One name, because there is one.</summary>
    private const string TemplateDatabase = "condux_template";

    // Always taken, never double-checked. Test collections run in parallel, so several classes ask for
    // their database at once and this is what stops four of them each starting a server. It was written
    // this way while the suite was still serial and the gate was doing nothing, because correctness
    // resting on a runner setting breaks silently the day someone changes the setting.
    private static readonly SemaphoreSlim StartGate = new(1, 1);

    private static PostgreSqlContainer? _container;

    /// <summary>Creates an empty, fully migrated database and returns its connection string.</summary>
    public static async Task<string> CreateDatabaseAsync()
    {
        var server = await StartAsync();

        // Generated rather than named after the test class, which a fixture has no way to learn. Nothing
        // a caller supplies reaches the DDL below, which takes an identifier and so cannot be
        // parameterised. Postgres allows 63 bytes for one; this is 33.
        var database = "t" + Guid.NewGuid().ToString("n");

        await using var conn = new NpgsqlConnection(server);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"CREATE DATABASE \"{database}\" TEMPLATE \"{TemplateDatabase}\"", conn);
        await cmd.ExecuteNonQueryAsync();
        return ConnectTo(server, database);
    }

    private static async Task<string> StartAsync()
    {
        await StartGate.WaitAsync();
        try
        {
            if (_container is not null)
            {
                return _container.GetConnectionString();
            }

            // This server answers for the whole run rather than for one class, so the default of 100 is
            // nowhere near enough and raising it is load-bearing rather than defensive. Measured peaks
            // on the full suite: 117 as it stands, 288 before PostgresFixture disposed each class's
            // control-plane hosts, and 366 if it stops clearing the connection pool. 500 is chosen to
            // clear all three, since both of those safeguards are one edit away from being removed.
            //
            // The image's docker-entrypoint.sh prepends `postgres` to a command that begins with a
            // dash, so this arrives at the server as `postgres -c max_connections=500`.
            var container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithCommand("-c", "max_connections=500")
                .Build();
            await container.StartAsync();
            await BuildTemplateAsync(container.GetConnectionString());

            // Assigned last, and held in a static rather than a local: the field is what keeps the
            // container object reachable, and a failure above must leave it null so the next class
            // starts a server again instead of being handed one with no template on it.
            _container = container;
            return container.GetConnectionString();
        }
        finally
        {
            StartGate.Release();
        }
    }

    private static async Task BuildTemplateAsync(string server)
    {
        await using (var conn = new NpgsqlConnection(server))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{TemplateDatabase}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        // Pooling off for this one connection. Npgsql holds a closed connection open in its pool, and
        // CREATE DATABASE ... TEMPLATE refuses a template that any session is still connected to, so a
        // pooled connection here would fail every copy that follows.
        await ApplyMigrationsAsync(ConnectTo(server, TemplateDatabase, pooling: false));
    }

    /// <summary>Applies the copied Postgres migration files, in filename order, over one connection.</summary>
    private static async Task ApplyMigrationsAsync(string connectionString)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "migrations");
        var files = Directory.GetFiles(dir, "*.sql").OrderBy(f => f, StringComparer.Ordinal);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        foreach (var file in files)
        {
            var sql = await File.ReadAllTextAsync(file);
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static string ConnectTo(string server, string database, bool pooling = true) =>
        new NpgsqlConnectionStringBuilder(server) { Database = database, Pooling = pooling }
            .ConnectionString;
}
