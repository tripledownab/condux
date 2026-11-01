using System.Text;
using Condux.IntegrationTests.Fixtures;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Drives deploy/migrations/apply-postgres.sh, the script compose's postgres-init and the Helm job both run,
/// inside a real Postgres container. Each fixture migration appends a row to <c>runs</c> every time it is
/// executed, so a test reads exactly which files ran. Every test gets its own database on one server.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MigrationLedgerTest : IAsyncLifetime
{
    private const string Script = "/work/apply-postgres.sh";

    private readonly PostgreSqlContainer _postgres =
        new PostgreSqlBuilder(ComposeImages.For("postgres")).Build();

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var script = Path.Combine(RepoFiles.FindRoot(), "deploy", "migrations", "apply-postgres.sh");
        await _postgres.CopyAsync(await File.ReadAllBytesAsync(script), Script);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task A_second_run_applies_nothing()
    {
        var db = await CreateDatabaseAsync();
        await WriteAsync(db, ("0001_a.sql", Probe("0001")), ("0002_b.sql", Probe("0002")));

        await ApplyAsync(db);
        var second = await ApplyAsync(db);

        Assert.Equal(new[] { "0001", "0002" }, await RunsAsync(db));
        Assert.Contains("0 applied, 2 skipped", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_edited_file_replays_every_file_from_the_first()
    {
        var db = await CreateDatabaseAsync();
        await WriteAsync(db, ("0001_a.sql", Probe("0001")), ("0002_b.sql", Probe("0002")), ("0003_c.sql", Probe("0003")));
        await ApplyAsync(db);

        await WriteAsync(db, ("0002_b.sql", Probe("0002") + "SELECT 2;\n"));
        await ApplyAsync(db);

        Assert.Equal(new[] { "0001", "0001", "0002", "0002", "0003", "0003" }, await RunsAsync(db));
    }

    [Fact]
    public async Task A_new_file_between_recorded_ones_replays_every_file_from_the_first()
    {
        var db = await CreateDatabaseAsync();
        await WriteAsync(db, ("0001_a.sql", Probe("0001")), ("0003_c.sql", Probe("0003")));
        await ApplyAsync(db);

        await WriteAsync(db, ("0002_b.sql", Probe("0002")));
        await ApplyAsync(db);

        Assert.Equal(new[] { "0001", "0001", "0002", "0003", "0003" }, await RunsAsync(db));
    }

    // An edited file runs where a replay of everything would run it: after the files before it have put
    // back what a later one removes. Started at the edited file instead, it would meet the database without
    // the column 0001 adds and 0003 drops, and fail on every deploy until someone edited 0001.
    [Fact]
    public async Task An_edited_file_sees_what_the_files_before_it_establish()
    {
        var db = await CreateDatabaseAsync();
        await WriteAsync(db,
            ("0001_a.sql", "CREATE TABLE IF NOT EXISTS t (id INT);\nALTER TABLE t ADD COLUMN IF NOT EXISTS c INT;\n"),
            ("0002_b.sql", "SELECT 1;\n"),
            ("0003_c.sql", "ALTER TABLE t DROP COLUMN IF EXISTS c;\n"));
        await ApplyAsync(db);

        await WriteAsync(db, ("0002_b.sql", "INSERT INTO t (c) SELECT 0 WHERE NOT EXISTS (SELECT 1 FROM t);\n"));
        await ApplyAsync(db);

        Assert.Equal("1", await ScalarAsync(db, "SELECT count(*)::text FROM t"));
    }

    // The same for the last file: nothing recorded sorts after it, but it is an edit, not an addition, and
    // only replaying every recorded file brings the database back to where it is.
    [Fact]
    public async Task An_edited_last_file_sees_what_the_files_before_it_establish()
    {
        var db = await CreateDatabaseAsync();
        await WriteAsync(db,
            ("0001_a.sql", "CREATE TABLE IF NOT EXISTS t (id INT);\nALTER TABLE t ADD COLUMN IF NOT EXISTS c INT;\n"),
            ("0002_b.sql", "ALTER TABLE t DROP COLUMN IF EXISTS c;\n"));
        await ApplyAsync(db);

        await WriteAsync(db, ("0002_b.sql",
            "INSERT INTO t (c) SELECT 0 WHERE NOT EXISTS (SELECT 1 FROM t);\nALTER TABLE t DROP COLUMN IF EXISTS c;\n"));
        await ApplyAsync(db);

        Assert.Equal("1", await ScalarAsync(db, "SELECT count(*)::text FROM t"));
    }

    // A full replay that fails at a file it had already recorded is replayed again by the next run. Without
    // the marker the edit that started it read as recorded, every file did, and the column 0002 adds and
    // 0003 drops stayed behind for good.
    [Fact]
    public async Task A_full_replay_that_fails_part_way_is_replayed_by_the_next_run()
    {
        var db = await CreateDatabaseAsync();
        await WriteAsync(db,
            ("0001_a.sql", "CREATE TABLE IF NOT EXISTS t (id INT);\nCREATE TABLE IF NOT EXISTS gate (x INT);\n"),
            ("0002_b.sql", "ALTER TABLE t ADD COLUMN IF NOT EXISTS a INT;\n"),
            ("0003_c.sql", "SELECT 1 / (SELECT count(*) FROM gate)::int;\nALTER TABLE t DROP COLUMN IF EXISTS a;\n"));
        await ScalarAsync(db, "CREATE TABLE gate (x INT); INSERT INTO gate VALUES (1)");
        await ApplyAsync(db);

        await ScalarAsync(db, "DELETE FROM gate");
        await WriteAsync(db, ("0001_a.sql",
            "CREATE TABLE IF NOT EXISTS t (id INT);\nCREATE TABLE IF NOT EXISTS gate (x INT);\nSELECT 2;\n"));
        var failed = await ExecAsync(db, Script, $"/work/{db}");
        Assert.NotEqual(0, failed.ExitCode);

        await ScalarAsync(db, "INSERT INTO gate VALUES (1)");
        await ApplyAsync(db);

        Assert.Null(await ScalarAsync(db,
            "SELECT column_name::text FROM information_schema.columns WHERE table_name = 't' AND column_name = 'a'"));
        Assert.Contains("0 applied", await ApplyAsync(db), StringComparison.Ordinal);
    }

    // A recorded file gone from the directory replays what is left, as a replay of everything would have,
    // and its row goes, so the run after applies nothing.
    [Fact]
    public async Task A_removed_file_replays_the_rest_once()
    {
        var db = await CreateDatabaseAsync();
        await WriteAsync(db, ("0001_a.sql", Probe("0001")), ("0002_b.sql", Probe("0002")));
        await ApplyAsync(db);

        await _postgres.ExecAsync(["rm", $"/work/{db}/0002_b.sql"]);
        await ApplyAsync(db);
        var third = await ApplyAsync(db);

        Assert.Equal(new[] { "0001", "0001", "0002" }, await RunsAsync(db));
        Assert.Contains("0 applied, 1 skipped", third, StringComparison.Ordinal);
    }

    // Rewording a whole-line comment changes nothing a file does, so it replays nothing.
    [Fact]
    public async Task A_comment_edit_replays_nothing()
    {
        var db = await CreateDatabaseAsync();
        await WriteAsync(db, ("0001_a.sql", "-- first\n" + Probe("0001")), ("0002_b.sql", Probe("0002")));
        await ApplyAsync(db);

        await WriteAsync(db, ("0001_a.sql", "-- reworded\n  -- and longer\n" + Probe("0001")));
        var second = await ApplyAsync(db);

        Assert.Equal(new[] { "0001", "0002" }, await RunsAsync(db));
        Assert.Contains("0 applied, 2 skipped", second, StringComparison.Ordinal);
    }

    // A blank line inside a string literal is data, so adding one is an edit like any other.
    [Fact]
    public async Task A_blank_line_inside_a_literal_is_an_edit()
    {
        var db = await CreateDatabaseAsync();
        await WriteAsync(db, ("0001_a.sql",
            "CREATE TABLE IF NOT EXISTS notes (v TEXT);\nDELETE FROM notes;\nINSERT INTO notes VALUES ('a\nb');\n"));
        await ApplyAsync(db);

        await WriteAsync(db, ("0001_a.sql",
            "CREATE TABLE IF NOT EXISTS notes (v TEXT);\nDELETE FROM notes;\nINSERT INTO notes VALUES ('a\n\nb');\n"));
        await ApplyAsync(db);

        Assert.Equal("a\n\nb", await ScalarAsync(db, "SELECT v FROM notes"));
    }

    // Names are matched as whole lines, so a name holding a newline would match the wrong ones.
    [Fact]
    public async Task A_name_holding_a_newline_is_refused_before_the_database_is_touched()
    {
        var db = await CreateDatabaseAsync();
        await WriteAsync(db, ("0001_a.sql", Probe("0001")), ("0002_x\ny.sql", Probe("0002")));

        var result = await ExecAsync(db, Script, $"/work/{db}");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Null(await ScalarAsync(db, "SELECT to_regclass('condux_migrations')::text"));
    }

    // A recorded name that merely ends like a new one does not make the new one an edit.
    [Fact]
    public async Task A_new_file_whose_name_ends_like_a_recorded_one_runs_alone()
    {
        var db = await CreateDatabaseAsync();
        await WriteAsync(db, ("0001_a.sql", Probe("0001")));
        await ApplyAsync(db);

        await WriteAsync(db, ("1_a.sql", Probe("1")));
        await ApplyAsync(db);

        Assert.Equal(new[] { "0001", "1" }, await RunsAsync(db));
    }

    [Fact]
    public async Task A_new_file_is_the_only_one_applied()
    {
        var db = await CreateDatabaseAsync();
        await WriteAsync(db, ("0001_a.sql", Probe("0001")), ("0002_b.sql", Probe("0002")));
        await ApplyAsync(db);

        await WriteAsync(db, ("0003_c.sql", Probe("0003")));
        await ApplyAsync(db);

        Assert.Equal(new[] { "0001", "0002", "0003" }, await RunsAsync(db));
    }

    // The file fails on the state of the database, not on its own text, so the run that retries it reads
    // the same content: only a file left unrecorded by its failure is run again.
    [Fact]
    public async Task A_failed_file_stops_the_run_and_is_tried_again_by_the_next()
    {
        var db = await CreateDatabaseAsync();
        const string failsUntilGateIsSet =
            "CREATE TABLE IF NOT EXISTS gate (x INT);\nSELECT 1 / (SELECT count(*) FROM gate)::int;\n";
        await WriteAsync(db, ("0001_a.sql", Probe("0001")), ("0002_b.sql", failsUntilGateIsSet + Probe("0002")),
            ("0003_c.sql", Probe("0003")));

        var failed = await ExecAsync(db, Script, $"/work/{db}");
        Assert.NotEqual(0, failed.ExitCode);
        Assert.Equal(new[] { "0001" }, await RunsAsync(db));

        await ScalarAsync(db, "INSERT INTO gate VALUES (1)");
        await ApplyAsync(db);

        Assert.Equal(new[] { "0001", "0002", "0003" }, await RunsAsync(db));
    }

    [Fact]
    public async Task An_empty_directory_is_refused_before_the_database_is_touched()
    {
        var db = await CreateDatabaseAsync();
        await _postgres.ExecAsync(["mkdir", "-p", $"/work/{db}"]);

        var result = await ExecAsync(db, Script, $"/work/{db}");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Null(await ScalarAsync(db, "SELECT to_regclass('condux_migrations')::text"));
    }

    // The reason for the ledger, on the real migrations: a replay used to take each table's lock
    // whether or not the file changed, so it waited behind any open transaction on that table. With
    // every file recorded, a second run takes no lock on an application table (it reads only the ledger),
    // which a lock_timeout makes measurable without waiting: a run that asked for the issues table's lock
    // would fail after two seconds.
    [Fact]
    public async Task The_repository_migrations_run_again_without_waiting_on_a_held_table()
    {
        var db = await CreateDatabaseAsync();
        var migrations = Path.Combine(RepoFiles.FindRoot(), "migrations", "postgres");
        foreach (var file in Directory.GetFiles(migrations, "*.sql"))
        {
            await WriteAsync(db, (Path.GetFileName(file), await File.ReadAllTextAsync(file)));
        }

        await ApplyAsync(db);

        await using var conn = new NpgsqlConnection(ConnectionTo(db));
        await conn.OpenAsync();
        await using var hold = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand("LOCK TABLE issues IN ACCESS SHARE MODE", conn, hold))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        var second = await ExecAsync(db, Script, $"/work/{db}", "PGOPTIONS='-c lock_timeout=2s'");

        Assert.True(second.ExitCode == 0, second.Stderr);
        Assert.Contains(" 0 applied,", second.Stdout, StringComparison.Ordinal);
    }

    // A file that records each time it is executed, and creates the table it records into on first use.
    private static string Probe(string name) =>
        $"CREATE TABLE IF NOT EXISTS runs (n SERIAL PRIMARY KEY, file TEXT NOT NULL);\nINSERT INTO runs (file) VALUES ('{name}');\n";

    private async Task<string> CreateDatabaseAsync()
    {
        var db = "t" + Guid.NewGuid().ToString("n");
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{db}\"", conn);
        await cmd.ExecuteNonQueryAsync();
        return db;
    }

    // Each database's migrations sit in a directory named after it.
    private async Task WriteAsync(string db, params (string File, string Sql)[] files)
    {
        foreach (var (file, sql) in files)
        {
            await _postgres.CopyAsync(Encoding.UTF8.GetBytes(sql), $"/work/{db}/{file}");
        }
    }

    private async Task<string> ApplyAsync(string db)
    {
        var result = await ExecAsync(db, Script, $"/work/{db}");
        Assert.True(result.ExitCode == 0, result.Stderr);
        return result.Stdout;
    }

    // Inside the container, over its local socket, as the server's own superuser.
    private Task<DotNet.Testcontainers.Containers.ExecResult> ExecAsync(
        string db, string script, string dir, string extraEnv = "") =>
        _postgres.ExecAsync(["sh", "-c", $"{extraEnv} PGUSER=postgres PGDATABASE={db} sh {script} {dir}"]);

    private async Task<string[]> RunsAsync(string db)
    {
        await using var conn = new NpgsqlConnection(ConnectionTo(db));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT file FROM runs ORDER BY file, n", conn);
        var runs = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            runs.Add(reader.GetString(0));
        }

        return [.. runs];
    }

    private async Task<string?> ScalarAsync(string db, string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionTo(db));
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private string ConnectionTo(string db) =>
        new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Database = db, Pooling = false }
            .ConnectionString;
}
