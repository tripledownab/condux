using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Condux.Core.SourceMaps;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Source-map upload over HTTP (ADR-0028): CI mints a release token, then a cookie-less client uploads a
/// .map with only <c>Authorization: Bearer</c>; the bytes land in the (faked) object store under the
/// debugId-derived key, a row indexes them, and the release lists them back. Also covers a bad token (401)
/// and the feature being off when object storage is unconfigured (404).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SourceMapApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string Map = "{\"version\":3,\"file\":\"app.js\",\"sources\":[\"app.ts\"],\"mappings\":\"AAAA\"}";

    // A control-plane with object storage "configured" (so the routes are live) but backed by an in-memory
    // fake, so the test never touches a real S3/MinIO. The factory registration is overridden, not invoked.
    private static WebApplicationFactory<Program> AppWithStore(string postgres, FakeObjectStore store) =>
        ControlPlaneApp.Create(postgres, configure: b =>
        {
            b.UseSetting("CONDUX_S3_ENDPOINT", "http://minio.invalid:9000");
            b.UseSetting("CONDUX_S3_BUCKET", "test-bucket");
            b.UseSetting("CONDUX_S3_ACCESS_KEY", "test");
            b.UseSetting("CONDUX_S3_SECRET_KEY", "test");
            b.ConfigureTestServices(s => s.AddSingleton<IObjectStore>(store));
        });

    // Not static: reaches pg.ConnectionString to set the tier out of band, since POST /api/orgs
    // always creates Free and only the Stripe webhook moves an org off it.
    private async Task<(HttpClient Admin, long ProjectId)> ProvisionAsync(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 2);
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "web", name = "Web", platform = "javascript" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
        return (client, projectId);
    }

    private static async Task<string> MintTokenAsync(HttpClient admin, long projectId)
    {
        var mint = await admin.PostAsJsonAsync($"/api/projects/{projectId}/release-tokens", new { name = "CI" });
        return (await mint.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private static HttpClient Bearer(WebApplicationFactory<Program> app, string token)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static ByteArrayContent MapContent()
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(Map));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    [Fact]
    public async Task Upload_ViaBearer_StoresBytes_IndexesRow()
    {
        var store = new FakeObjectStore();
        var app = AppWithStore(pg.ConnectionString, store);
        var (admin, projectId) = await ProvisionAsync(app);
        var token = await MintTokenAsync(admin, projectId);

        var ci = Bearer(app, token);
        var resp = await ci.PostAsync("/api/sourcemaps?release=1.4.2&filename=app.js&debugId=abc123", MapContent());
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("app.js", body.GetProperty("filename").GetString());
        Assert.Equal("abc123", body.GetProperty("debugId").GetString());
        Assert.Equal("1.4.2", body.GetProperty("release").GetString());
        Assert.Equal(Encoding.UTF8.GetByteCount(Map), body.GetProperty("byteSize").GetInt64());

        // The bytes landed in the object store under the debugId-derived key.
        var stored = Assert.Single(store.Objects);
        Assert.Equal($"sourcemaps/{projectId}/by-debug-id/abc123", stored.Key);
        Assert.Equal(Map, Encoding.UTF8.GetString(stored.Value));
    }

    [Fact]
    public async Task Upload_WithBadToken_Returns401()
    {
        var app = AppWithStore(pg.ConnectionString, new FakeObjectStore());
        var ci = Bearer(app, "condux_rel_nope");
        var resp = await ci.PostAsync("/api/sourcemaps?release=1.0.0&filename=app.js", MapContent());
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Upload_WhenObjectStoreNotConfigured_Returns404()
    {
        // No CONDUX_S3_* set, so the feature is off and the route 404s before auth.
        var app = ControlPlaneApp.Create(pg.ConnectionString);
        var resp = await app.CreateClient()
            .PostAsync("/api/sourcemaps?release=1.0.0&filename=app.js", MapContent());
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Upload_WithNonSourceMapBody_Returns400()
    {
        var app = AppWithStore(pg.ConnectionString, new FakeObjectStore());
        var (admin, projectId) = await ProvisionAsync(app);
        var token = await MintTokenAsync(admin, projectId);

        var ci = Bearer(app, token);
        var notAMap = new ByteArrayContent(Encoding.UTF8.GetBytes("this is not a source map"));
        notAMap.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var resp = await ci.PostAsync("/api/sourcemaps?release=1.0.0&filename=app.js", notAMap);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Upload_WithOverlongField_Returns400()
    {
        var app = AppWithStore(pg.ConnectionString, new FakeObjectStore());
        var (admin, projectId) = await ProvisionAsync(app);
        var token = await MintTokenAsync(admin, projectId);

        var ci = Bearer(app, token);
        var resp = await ci.PostAsync(
            $"/api/sourcemaps?release={new string('x', 300)}&filename=app.js", MapContent());
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    private sealed class FakeObjectStore : IObjectStore
    {
        public Dictionary<string, byte[]> Objects { get; } = new();

        public Task PutAsync(
            string key, byte[] content, string contentType, CancellationToken cancellationToken = default)
        {
            Objects[key] = content;
            return Task.CompletedTask;
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Objects.TryGetValue(key, out var value) ? value : null);
    }
}
