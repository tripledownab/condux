using System.Text;
using Condux.ControlPlane.SourceMaps;
using Condux.Core.Events;
using Condux.Core.SourceMaps;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// End-to-end read-time symbolication (ADR-0028): with a source map recorded in Postgres + (faked) object
/// storage, FrameSymbolicator rewrites a minified in-app frame to its original file/line/column + function
/// + source line, resolving the map by debugId (a debug_meta image matching the frame's abs_path). Also
/// verifies an unmatched frame is left untouched.
/// </summary>
[Trait("Category", "Integration")]
public sealed class FrameSymbolicatorTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    // The hand-verified map from the decoder tests: generated (line 1, col 5) -> src/app.ts (line 2, col 3), "boom".
    private const string Map =
        """{"version":3,"sources":["src/app.ts"],"sourcesContent":["const a = 1;\nfunction boom() {\n  throw new Error();\n}\n"],"names":["boom"],"mappings":"AAAA,CAEA;KAAGA"}""";

    [Fact]
    public async Task Symbolicates_a_minified_frame_via_debug_id()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var artifacts = new SourceMapArtifactRepository(pg.ConnectionString);

        const string objectKey = "sourcemaps/by-debug-id/abc123";
        await artifacts.RecordAsync(
            projectId, "app@1.4.2", dist: null, debugId: "abc123", filename: "app.min.js",
            objectKey: objectKey, byteSize: Map.Length);
        var symbolicator = new FrameSymbolicator(artifacts, new MapStore(Encoding.UTF8.GetBytes(Map), objectKey));

        var result = await symbolicator.SymbolicateAsync(projectId, MinifiedEvent(
            debugId: "abc123", codeFile: "https://app/js/app.min.js", absPath: "https://app/js/app.min.js"));

        var frame = Assert.Single(Assert.Single(result.Exceptions).Stacktrace!.Frames);
        Assert.Equal("src/app.ts", frame.Filename);
        Assert.Equal(3, frame.Lineno); // original line 2 (0-based) -> 1-based
        Assert.Equal(4, frame.Colno); // original column 3 (0-based) -> 1-based
        Assert.Equal("boom", frame.Function);
        Assert.Equal("  throw new Error();", frame.ContextLine);
    }

    [Fact]
    public async Task Leaves_a_frame_untouched_when_no_map_matches()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var symbolicator = new FrameSymbolicator(
            new SourceMapArtifactRepository(pg.ConnectionString), new MapStore([]));

        var result = await symbolicator.SymbolicateAsync(projectId, MinifiedEvent(
            debugId: "missing", codeFile: "https://app/js/app.min.js", absPath: "https://app/js/app.min.js"));

        Assert.Equal("app.min.js", Assert.Single(Assert.Single(result.Exceptions).Stacktrace!.Frames).Filename);
    }

    [Fact]
    public async Task Symbolicates_via_release_and_filename_basename_when_no_debug_id()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var artifacts = new SourceMapArtifactRepository(pg.ConnectionString);

        const string objectKey = "sourcemaps/by-release/app.min.js";
        await artifacts.RecordAsync(
            projectId, "app@1.4.2", dist: null, debugId: null, filename: "app.min.js",
            objectKey: objectKey, byteSize: Map.Length);
        var symbolicator = new FrameSymbolicator(artifacts, new MapStore(Encoding.UTF8.GetBytes(Map), objectKey));

        // No debug images; the frame's abs_path is the full deployed URL with a cache-buster query, so the
        // basename ("app.min.js") is what must line up with the uploaded map's filename.
        var stored = new Event
        {
            Release = "app@1.4.2",
            Exceptions =
            [
                new ExceptionValue
                {
                    Stacktrace = new Stacktrace
                    {
                        Frames =
                        [
                            new Frame
                            {
                                InApp = true, Filename = "app.min.js", Function = "n", Lineno = 2, Colno = 6,
                                AbsPath = "https://app.example.com/_next/static/chunks/app.min.js?v=abc123",
                            },
                        ],
                    },
                },
            ],
        };

        var result = await symbolicator.SymbolicateAsync(projectId, stored);

        var frame = Assert.Single(Assert.Single(result.Exceptions).Stacktrace!.Frames);
        Assert.Equal("src/app.ts", frame.Filename);
        Assert.Equal(3, frame.Lineno);
        Assert.Equal("boom", frame.Function);
    }

    // The frame list is written by whoever sent the event and is only as short as they choose, so what
    // matters is that reading one issue costs a number of round trips that does not come from them. It
    // used to be one query per frame, which turned a single dashboard read into as many database round
    // trips as the event had frames. Counted through the server's own committed-transaction counter,
    // because each repository call opens its own connection and runs one statement.
    [Fact]
    public async Task A_long_stack_costs_the_same_round_trips_as_a_short_one()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        // One pooled connection, so the repository and the counter share a backend. Postgres aggregates
        // xact_commit per database but each backend flushes its own pending statistics on its own timer,
        // so a count read from a second backend reads the same whatever the first one did.
        var counted = $"{pg.ConnectionString};Maximum Pool Size=1";
        var artifacts = new SourceMapArtifactRepository(counted);
        var frames = FramesNamingDistinctFiles(500);
        var crashSite = frames[^1].Filename!;
        // One real artifact, so the walk over the other 499 frames actually happens: an event that can
        // reach no map at all returns before the frame loop, and a test written against that one cannot
        // see a query put back inside the loop.
        await artifacts.RecordAsync(
            projectId, "app@1.4.2", dist: null, debugId: null, filename: crashSite,
            objectKey: $"sourcemaps/{crashSite}", byteSize: Map.Length);
        var symbolicator = new FrameSymbolicator(
            artifacts, new MapStore(Encoding.UTF8.GetBytes(Map), $"sourcemaps/{crashSite}"));

        await CommittedTransactionsAsync(counted); // settle the setup's own statistics before the baseline
        var before = await CommittedTransactionsAsync(counted);
        var result = await symbolicator.SymbolicateAsync(projectId, EventWithFrames(frames));
        var spent = await CommittedTransactionsAsync(counted) - before;

        Assert.Equal("src/app.ts", Assert.Single(result.Exceptions).Stacktrace!.Frames[^1].Filename);
        // One lookup for the event plus the counting read. The number to notice is that it is not 500:
        // measured at 433 with a single lookup put back inside the frame loop.
        Assert.True(spent < 20, $"symbolicating 500 frames committed {spent} transactions");
    }

    // When the cap bites it must drop the entry points, not the failure: frames arrive oldest-first, so
    // the crash site is the last one and is the frame a reader opened the issue to look at.
    [Fact]
    public async Task Symbolicates_the_crash_site_when_a_stack_names_more_files_than_the_cap()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var artifacts = new SourceMapArtifactRepository(pg.ConnectionString);
        var frames = FramesNamingDistinctFiles(200);
        var oldest = frames[0].Filename!;
        var crashSite = frames[^1].Filename!;

        foreach (var filename in new[] { oldest, crashSite })
        {
            await artifacts.RecordAsync(
                projectId, "app@1.4.2", dist: null, debugId: null, filename: filename,
                objectKey: $"sourcemaps/{filename}", byteSize: Map.Length);
        }

        var symbolicator = new FrameSymbolicator(
            artifacts, new MapStore(Encoding.UTF8.GetBytes(Map), $"sourcemaps/{oldest}", $"sourcemaps/{crashSite}"));
        var result = await symbolicator.SymbolicateAsync(projectId, EventWithFrames(frames));

        var got = Assert.Single(result.Exceptions).Stacktrace!.Frames;
        Assert.Equal("src/app.ts", got[^1].Filename); // the crash site was resolved
        Assert.Equal(oldest, got[0].Filename); // the far end of the stack was left alone
    }

    // The dashboard reads the LAST exception as the primary one, so when the cap bites it must not be
    // that one that loses its maps. Collecting forwards let a noisy earlier exception in the chain fill
    // the cap and leave the frames a reader opened the issue to see unresolved.
    [Fact]
    public async Task Symbolicates_the_primary_exception_when_an_earlier_one_fills_the_cap()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var artifacts = new SourceMapArtifactRepository(pg.ConnectionString);
        await artifacts.RecordAsync(
            projectId, "app@1.4.2", dist: null, debugId: null, filename: "primary.js",
            objectKey: "sourcemaps/primary.js", byteSize: Map.Length);

        var stored = new Event
        {
            Release = "app@1.4.2",
            Exceptions =
            [
                new ExceptionValue
                {
                    Stacktrace = new Stacktrace { Frames = FramesNamingDistinctFiles(200) },
                },
                new ExceptionValue
                {
                    Stacktrace = new Stacktrace
                    {
                        Frames =
                        [
                            new Frame
                            {
                                InApp = true, Filename = "primary.js", Function = "n", Lineno = 2, Colno = 6,
                                AbsPath = "https://app.example.com/_next/static/chunks/primary.js",
                            },
                        ],
                    },
                },
            ],
        };

        var symbolicator = new FrameSymbolicator(
            artifacts, new MapStore(Encoding.UTF8.GetBytes(Map), "sourcemaps/primary.js"));
        var result = await symbolicator.SymbolicateAsync(projectId, stored);

        Assert.Equal("src/app.ts", result.Exceptions[^1].Stacktrace!.Frames[0].Filename);
    }

    // Re-uploading a chunk leaves the old artifact in place, so resolution has to take the newest row
    // for a key. The batched lookups say that with DISTINCT ON, which is only correct while the ORDER
    // BY leads with the same expression and then by created_at: reorder it and Postgres picks an
    // arbitrary row of the group and nothing anywhere fails. created_at is set explicitly here, because
    // two inserts a few microseconds apart is not a reliable way to ask which one is newer.
    [Fact]
    public async Task Resolves_the_newest_artifact_when_a_chunk_was_uploaded_twice()
    {
        var projectId = await ProjectSeed.CreateProjectAsync(pg.ConnectionString);
        var artifacts = new SourceMapArtifactRepository(pg.ConnectionString);
        await RecordAtAsync(projectId, "app.min.js", "sourcemaps/stale", DateTimeOffset.UtcNow.AddDays(-1));
        await RecordAtAsync(projectId, "app.min.js", "sourcemaps/fresh", DateTimeOffset.UtcNow);

        // The store knows only the newer object, so a frame is rewritten iff that is the one resolved.
        var symbolicator = new FrameSymbolicator(
            artifacts, new MapStore(Encoding.UTF8.GetBytes(Map), "sourcemaps/fresh"));
        var result = await symbolicator.SymbolicateAsync(projectId, EventWithFrames(
        [
            new Frame
            {
                InApp = true, Filename = "app.min.js", Function = "n", Lineno = 2, Colno = 6,
                AbsPath = "https://app.example.com/_next/static/chunks/app.min.js",
            },
        ]));

        Assert.Equal("src/app.ts", Assert.Single(Assert.Single(result.Exceptions).Stacktrace!.Frames).Filename);
    }

    private async Task RecordAtAsync(long projectId, string filename, string objectKey, DateTimeOffset createdAt)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO sourcemap_artifacts
                (id, project_id, release, dist, debug_id, filename, object_key, byte_size, created_at)
            VALUES (@id, @project, 'app@1.4.2', NULL, NULL, @filename, @key, 0, @created);
            """, conn);
        cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
        cmd.Parameters.AddWithValue("project", projectId);
        cmd.Parameters.AddWithValue("filename", filename);
        cmd.Parameters.AddWithValue("key", objectKey);
        cmd.Parameters.AddWithValue("created", createdAt);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CommittedTransactionsAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        // Forced, because a backend flushes its pending statistics on a timer of its own: without this
        // a burst of fast transactions is still pending when the counter is read, and the count reads
        // the same either way, which is a test that cannot fail.
        await using var cmd = new NpgsqlCommand(
            """
            SELECT pg_stat_force_next_flush(), xact_commit FROM pg_stat_database
            WHERE datname = current_database();
            """, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return reader.GetInt64(1);
    }

    private static Frame[] FramesNamingDistinctFiles(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Frame
        {
            InApp = true, Filename = $"chunk{i}.js", Function = "n", Lineno = 2, Colno = 6,
            AbsPath = $"https://app.example.com/_next/static/chunks/chunk{i}.js",
        })];

    private static Event EventWithFrames(IReadOnlyList<Frame> frames) => new()
    {
        Release = "app@1.4.2",
        Exceptions = [new ExceptionValue { Stacktrace = new Stacktrace { Frames = frames } }],
    };

    private static Event MinifiedEvent(string debugId, string codeFile, string absPath) => new()
    {
        Release = "app@1.4.2",
        DebugImages = [new DebugImage { Type = "sourcemap", CodeFile = codeFile, DebugId = debugId }],
        Exceptions =
        [
            new ExceptionValue
            {
                Type = "TypeError",
                Stacktrace = new Stacktrace
                {
                    Frames =
                    [
                        new Frame
                        {
                            InApp = true, Filename = "app.min.js", AbsPath = absPath,
                            Function = "n", Lineno = 2, Colno = 6,
                        },
                    ],
                },
            },
        ],
    };

    // Serves the same map under every key it is given. Which keys it holds decides what a test can
    // tell apart: a store that knows only one of two recorded artifacts cannot show whether the second
    // was looked up or merely failed to load.
    private sealed class MapStore(byte[] bytes, params string[] keys) : IObjectStore
    {
        public Task PutAsync(string k, byte[] content, string contentType, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<byte[]?> GetAsync(string k, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(keys.Contains(k) ? bytes : null);
    }
}
