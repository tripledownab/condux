using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.Grouping;
using Condux.Core.Plans;
using Condux.Core.SourceMaps;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The MCP get_issue tool de-minifies stored frames through the same read-time symbolication (ADR-0028) the
/// dashboard uses — the shared EventSymbolication service. Seeds a minified in-app frame + a source-map
/// image (like SourceMapSymbolicationApiTest) but reads it back over the MCP endpoint with a scoped token,
/// asserting the returned event carries the ORIGINAL source position.
/// </summary>
[Trait("Category", "Integration")]
public sealed class McpSymbolicationApiTest(PostgresFixture pg, ClickHouseFixture ch)
    : IClassFixture<PostgresFixture>, IClassFixture<ClickHouseFixture>
{
    private const string Map =
        """{"version":3,"sources":["src/app.ts"],"sourcesContent":["const a = 1;\nfunction boom() {\n  throw new Error();\n}\n"],"names":["boom"],"mappings":"AAAA,CAEA;KAAGA"}""";

    [Fact]
    public async Task EventTools_deminify_the_stored_frame()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);

        const string objectKey = "sourcemaps/by-debug-id/abc123";
        var store = new MapStore(objectKey, Encoding.UTF8.GetBytes(Map));
        var app = ControlPlaneApp.Create(pg.ConnectionString, ch, configure: b =>
        {
            b.UseSetting("CONDUX_S3_ENDPOINT", "http://minio.invalid:9000");
            b.UseSetting("CONDUX_S3_BUCKET", "test");
            b.UseSetting("CONDUX_S3_ACCESS_KEY", "test");
            b.UseSetting("CONDUX_S3_SECRET_KEY", "test");
            b.ConfigureTestServices(s => s.AddSingleton<IObjectStore>(store));
        });
        var client = app.CreateClient();

        await ApiAuth.SignUpAsync(client);
        var orgId = (await (await client.PostAsJsonAsync("/api/orgs",
                new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme", tier = 2 }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var projectId = (await (await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
                new { name = "Web", platform = "javascript" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project").GetProperty("id").GetInt64();

        await new SourceMapArtifactRepository(pg.ConnectionString).RecordAsync(
            projectId, "app@1.4.2", dist: null, debugId: "abc123", filename: "app.min.js",
            objectKey: objectKey, byteSize: Map.Length);

        var grouping = new Grouping("fp-symbol", "TypeError: boom", "run");
        var issue = await new IssueRepository(pg.ConnectionString)
            .UpsertAsync(projectId, grouping, Level.Error, DateTimeOffset.UtcNow);

        using var chHttp = new HttpClient();
        ClickHouseRegistration.Configure(chHttp, ch.BaseUrl, ch.ChUser, ch.ChPassword);
        var stored = new Event
        {
            EventId = Guid.NewGuid().ToString("N"),
            Level = Level.Error,
            Message = "boom",
            Release = "app@1.4.2",
            TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DebugImages =
                [new DebugImage { Type = "sourcemap", CodeFile = "https://app/app.min.js", DebugId = "abc123" }],
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
                                InApp = true, Filename = "app.min.js", AbsPath = "https://app/app.min.js",
                                Function = "n", Lineno = 2, Colno = 6,
                            },
                        ],
                    },
                },
            ],
        };
        await new ClickHouseEventWriter(chHttp).InsertAsync([ClickHouseEventWriter.ToRow(
            projectId.ToString(), (ulong)issue.Id, stored, grouping.Fingerprint,
            PlanCatalog.For(Tier.Free).RetentionDays)]);

        // Mint an MCP token and call get_issue over /api/mcp.
        var raw = (await (await client.PostAsJsonAsync($"/api/projects/{projectId}/mcp-tokens", new { name = "x" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var agent = app.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", raw);

        var rpc = await (await agent.PostAsJsonAsync("/api/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "get_issue", arguments = new { issueId = issue.PublicId.ToString() } },
        })).Content.ReadFromJsonAsync<JsonElement>();

        var toolText = rpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        var frame = FrameOf(JsonDocument.Parse(toolText).RootElement
            .GetProperty("latestEvent").GetProperty("payload").GetString()!);
        Assert.Equal("src/app.ts", frame.GetProperty("Filename").GetString());
        Assert.Equal(3, frame.GetProperty("Lineno").GetInt32());
        Assert.Equal("boom", frame.GetProperty("Function").GetString());

        // list_issue_events de-minifies too (the same shared symbolication path).
        var eventsRpc = await (await agent.PostAsJsonAsync("/api/mcp", new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "tools/call",
            @params = new
            {
                name = "list_issue_events",
                arguments = new { issueId = issue.PublicId.ToString(), limit = 10, offset = 0 },
            },
        })).Content.ReadFromJsonAsync<JsonElement>();
        var eventsText = eventsRpc.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        var listed = JsonDocument.Parse(eventsText).RootElement.GetProperty("events");
        Assert.Equal(1, listed.GetArrayLength());
        Assert.Equal("src/app.ts",
            FrameOf(listed[0].GetProperty("payload").GetString()!).GetProperty("Filename").GetString());
    }

    // The first stack frame of a stored event payload (default-serialized Event, so PascalCase inside).
    private static JsonElement FrameOf(string payload) =>
        JsonDocument.Parse(payload).RootElement
            .GetProperty("Exceptions")[0].GetProperty("Stacktrace").GetProperty("Frames")[0];

    private sealed class MapStore(string key, byte[] bytes) : IObjectStore
    {
        public Task PutAsync(string k, byte[] content, string contentType, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<byte[]?> GetAsync(string k, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(k == key ? bytes : null);
    }
}
