using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Condux.Core.Events;
using Condux.Core.FixEngine;
using Condux.Core.Grouping;
using Condux.Core.Plans;
using Condux.Core.SourceControl;
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
        var (client, project) = await SeedMinifiedIssueAsync(Map, new CapturingFixPublisher());

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

    [Fact]
    public async Task RequestFix_scopes_the_fix_to_the_file_the_source_map_names()
    {
        // The Conductor reads what the issue page shows. Before, it read the stored frame, which names a
        // minified chunk no repository holds, so the fix could never be scoped to the source file. The
        // source is written the way Turbopack writes it, relative to the workspace root, so no code
        // mapping is needed.
        var publisher = new CapturingFixPublisher();
        var (client, project) = await SeedMinifiedIssueAsync(
            Map.Replace("\"src/app.ts\"", "\"turbopack:///[project]/web/src/app.ts\"", StringComparison.Ordinal),
            publisher);
        await client.PostAsJsonAsync($"/api/projects/{project}/repos", new { repoFullName = "acme/web" });
        var publicId = (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/issues"))
            .GetProperty("issues")[0].GetProperty("id").GetString();

        var resp = await client.PostAsJsonAsync($"/api/projects/{project}/issues/{publicId}/fix", new { });

        Assert.Equal(System.Net.HttpStatusCode.Accepted, resp.StatusCode);
        Assert.Equal(["web/src/app.ts"], publisher.Job!.ScopedPaths);
    }

    [Fact]
    public async Task RequestFix_hands_the_run_back_when_the_client_hangs_up_while_maps_load()
    {
        // Nothing has been sent when the request is abandoned during a read, so the run it reserved is
        // refunded, even though the refund happens after the client has gone.
        var store = new HangingMapStore();
        var (client, project) = await SeedMinifiedIssueAsync(Map, new CapturingFixPublisher(), store);
        await client.PostAsJsonAsync($"/api/projects/{project}/repos", new { repoFullName = "acme/web" });
        var publicId = (await client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/issues"))
            .GetProperty("issues")[0].GetProperty("id").GetString();
        var orgId = (await new ProjectRepository(pg.ConnectionString).GetAsync(long.Parse(project)))!.OrgId;
        var quota = new PostgresAiFixQuota(pg.ConnectionString);
        using var hangUp = new CancellationTokenSource();

        var request = client.PostAsJsonAsync(
            $"/api/projects/{project}/issues/{publicId}/fix", new { }, hangUp.Token);
        await store.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, await quota.GetUsedAsync(orgId, DateTimeOffset.UtcNow));
        await hangUp.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        // The server finishes after the client has gone, so wait a bounded time for the refund to land.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await quota.GetUsedAsync(orgId, DateTimeOffset.UtcNow) != 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the reserved run was never refunded");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task SuggestedMappings_derive_from_the_source_the_map_names()
    {
        // The stored frame names the minified chunk, which no repository holds, so deriving from it could
        // never suggest anything. The map names the source relative to the app, as webpack writes it, and
        // the repository keeps that app under web/, which is the rule a user would otherwise write by hand.
        var (client, project) = await SeedMinifiedIssueAsync(
            Map.Replace("\"src/app.ts\"", "\"webpack://checkout/./app/src/app.ts\"", StringComparison.Ordinal),
            new CapturingFixPublisher(),
            services: s => AddSourceHost(s, new TreeHost(["web/src/app.ts", "README.md"])));
        var repoId = await LinkRepoAsync(client, project);

        var suggestions = await client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{project}/repos/{repoId}/suggested-mappings");

        var only = Assert.Single(suggestions.EnumerateArray());
        Assert.Equal("app/", only.GetProperty("stackRoot").GetString());
        Assert.Equal("web/", only.GetProperty("sourceRoot").GetString());
    }

    [Fact]
    public async Task SuggestedMappings_answer_none_when_the_source_host_times_out()
    {
        // HttpClient reports its own timeout as a cancellation, which used to escape as a 500.
        var (client, project) = await SeedMinifiedIssueAsync(
            Map, new CapturingFixPublisher(),
            services: s => AddSourceHost(s, new TreeHost(timesOut: true)));
        var repoId = await LinkRepoAsync(client, project);

        var resp = await client.GetAsync($"/api/projects/{project}/repos/{repoId}/suggested-mappings");

        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty((await resp.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
    }

    [Fact]
    public async Task SuggestedMappings_answer_within_their_budget_when_the_object_store_hangs()
    {
        // The list overload waits for maps without a deadline. The endpoint bounds it, and past the budget
        // it derives from the stored frames instead: the tree holds the minified chunk under web/, so the
        // raw frame app.min.js still yields a rule, which only the fallback can produce.
        var store = new HangingMapStore();
        var (client, project) = await SeedMinifiedIssueAsync(
            Map, new CapturingFixPublisher(), store,
            services: s => AddSourceHost(s, new TreeHost(["web/app.min.js"])));
        var repoId = await LinkRepoAsync(client, project);
        // Well past the budget: a request still waiting then has not been bounded.
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var resp = await client.GetAsync(
            $"/api/projects/{project}/repos/{repoId}/suggested-mappings", giveUp.Token);

        Assert.True(store.Reached.Task.IsCompleted, "the map was never looked up, so nothing was bounded");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var only = Assert.Single((await resp.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal("", only.GetProperty("stackRoot").GetString());
        Assert.Equal("web/", only.GetProperty("sourceRoot").GetString());
    }

    private static void AddSourceHost(IServiceCollection services, TreeHost host)
    {
        services.AddSingleton<ISourceHostTokens, StubTokens>();
        services.AddSingleton<ISourceHostClient>(host);
    }

    // The org's installation (suggestions need a token) and the linked repository.
    private async Task<string> LinkRepoAsync(HttpClient client, string project)
    {
        var orgId = (await new ProjectRepository(pg.ConnectionString).GetAsync(long.Parse(project)))!.OrgId;
        await new GithubInstallationRepository(pg.ConnectionString)
            .LinkAsync(Random.Shared.NextInt64(1, long.MaxValue), orgId, "acme");
        var link = await client.PostAsJsonAsync($"/api/projects/{project}/repos",
            new { repoFullName = "acme/web", defaultBranch = "main" });
        return (await link.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private sealed class StubTokens : ISourceHostTokens
    {
        public Task<string> GetAsync(long installationId, CancellationToken ct = default) =>
            Task.FromResult("ghs_stub");
    }

    // A source host that only lists a tree, or times out doing it; nothing else is called here.
    private sealed class TreeHost(IReadOnlyList<string>? tree = null, bool timesOut = false) : ISourceHostClient
    {
        public Task<IReadOnlyList<string>> ListTreeAsync(
            string token, string repoFullName, string gitRef, CancellationToken ct = default) =>
            timesOut
                ? throw new TaskCanceledException("the source host timed out")
                : Task.FromResult(tree ?? []);

        public Task<RepoFile?> GetFileAsync(
            string token, string repoFullName, string path, string gitRef, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> GetBranchHeadShaAsync(
            string token, string repoFullName, string branch, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CreateBranchAsync(
            string token, string repoFullName, string branch, string fromSha, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task PutFileAsync(
            string token, string repoFullName, string path, string branch, string message, string content,
            string? existingSha, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListBranchesAsync(
            string token, string repoFullName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> ListInstallationRepositoriesAsync(
            string token, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SuspectCommit?> GetLatestCommitTouchingAsync(
            string token, string repoFullName, string path, string sha, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> OpenDraftPullRequestAsync(
            string token, string repoFullName, string headBranch, string baseBranch, string title, string body,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<FoundPullRequest?> FindPullRequestAsync(
            string token, string repoFullName, string headBranch, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // An org and project on a tier with fixes, a stored event whose one in-app frame is minified, and the
    // map for it behind a fake object store, so symbolication runs without an S3 server.
    private async Task<(HttpClient Client, string Project)> SeedMinifiedIssueAsync(
        string map, CapturingFixPublisher publisher, IObjectStore? store = null,
        Action<IServiceCollection>? services = null)
    {
        // Object storage configured (so symbolication runs) but backed by an in-memory fake holding the map.
        const string objectKey = "sourcemaps/by-debug-id/abc123";
        store ??= new InMemoryMapStore(Encoding.UTF8.GetBytes(map), objectKey);
        var app = ControlPlaneApp.Create(pg.ConnectionString, ch, configure: b =>
        {
            b.UseSetting("CONDUX_S3_ENDPOINT", "http://s3.invalid:9000");
            b.UseSetting("CONDUX_S3_BUCKET", "test");
            b.UseSetting("CONDUX_S3_ACCESS_KEY", "test");
            b.UseSetting("CONDUX_S3_SECRET_KEY", "test");
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<IObjectStore>(store);
                s.AddSingleton<IFixRequestPublisher>(publisher);
                services?.Invoke(s);
            });
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
            objectKey: objectKey, byteSize: map.Length);

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

        return (client, project);
    }
}
