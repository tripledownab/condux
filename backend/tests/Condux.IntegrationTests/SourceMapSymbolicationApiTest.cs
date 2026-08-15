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
/// End-to-end read-time symbolication over the HTTP endpoint (ADR-0028): seeds a grouped issue + a raw
/// event (a minified in-app frame + a source-map debug image) in ClickHouse, records the map artifact and a
/// fake object store holding it, then reads the issue detail and asserts the returned event payload carries
/// the ORIGINAL source position. Covers the getIssue symbolication glue that FrameSymbolicatorTest (which
/// calls the symbolicator directly) does not.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SourceMapSymbolicationApiTest(PostgresFixture pg, ClickHouseFixture ch)
    : IClassFixture<PostgresFixture>, IClassFixture<ClickHouseFixture>
{
    private const string Map =
        """{"version":3,"sources":["src/app.ts"],"sourcesContent":["const a = 1;\nfunction boom() {\n  throw new Error();\n}\n"],"names":["boom"],"mappings":"AAAA,CAEA;KAAGA"}""";

    [Fact]
    public async Task GetIssue_deminifies_the_stored_frame()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);

        // Object storage configured (so symbolication runs) but backed by an in-memory fake holding the map.
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
                new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2); // Business
        var projectId = (await (await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
                new { slug = "web", name = "Web", platform = "javascript" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project").GetProperty("id").GetInt64();
        var project = projectId.ToString();

        // Record the map artifact (resolved by debugId), matching the event's debug image below.
        await new SourceMapArtifactRepository(pg.ConnectionString).RecordAsync(
            projectId, "app@1.4.2", dist: null, debugId: "abc123", filename: "app.min.js",
            objectKey: objectKey, byteSize: Map.Length);

        // Seed a grouped issue + a raw event whose payload has a minified in-app frame + a source-map image.
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
            project, (ulong)issue.Id, stored, grouping.Fingerprint, PlanCatalog.For(Tier.Free).RetentionDays)]);

        // Read the issue detail; the returned payload's frame should be de-minified (default-serialized
        // Event, so PascalCase inside the payload string).
        var list = (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/issues"))
            .GetProperty("issues");
        var publicId = list[0].GetProperty("id").GetString();
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/issues/{publicId}");

        var payload = detail.GetProperty("events")[0].GetProperty("payload").GetString()!;
        var frame = JsonDocument.Parse(payload).RootElement
            .GetProperty("Exceptions")[0].GetProperty("Stacktrace").GetProperty("Frames")[0];
        Assert.Equal("src/app.ts", frame.GetProperty("Filename").GetString());
        Assert.Equal(3, frame.GetProperty("Lineno").GetInt32());
        Assert.Equal("boom", frame.GetProperty("Function").GetString());
    }

    private sealed class MapStore(string key, byte[] bytes) : IObjectStore
    {
        public Task PutAsync(string k, byte[] content, string contentType, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<byte[]?> GetAsync(string k, CancellationToken ct = default) =>
            Task.FromResult<byte[]?>(k == key ? bytes : null);
    }
}
