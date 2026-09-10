using System.Text;
using Condux.ControlPlane.SourceMaps;
using Condux.Core.Events;
using Condux.Core.SourceMaps;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
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
        var symbolicator = new FrameSymbolicator(artifacts, new MapStore(objectKey, Encoding.UTF8.GetBytes(Map)));

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
            new SourceMapArtifactRepository(pg.ConnectionString), new MapStore("none", []));

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
        var symbolicator = new FrameSymbolicator(artifacts, new MapStore(objectKey, Encoding.UTF8.GetBytes(Map)));

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

    private sealed class MapStore(string key, byte[] bytes) : IObjectStore
    {
        public Task PutAsync(string k, byte[] content, string contentType, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<byte[]?> GetAsync(string k, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(k == key ? bytes : null);
    }
}
