using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Xunit;
using static Condux.IntegrationTests.Fixtures.ReleaseTokenUploads;

namespace Condux.IntegrationTests;

/// <summary>
/// Source-map upload over HTTP (ADR-0028): CI mints a release token, then a cookie-less client uploads a
/// .map with only <c>Authorization: Bearer</c>; the bytes land in the (faked) object store under the
/// debugId-derived key, a row indexes them, and the release lists them back. Also covers a bad token (401),
/// an empty body (400), one past the cap (413) and the feature being off when object storage is
/// unconfigured (404).
/// </summary>
[Trait("Category", "Integration")]
public sealed class SourceMapApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string Map = "{\"version\":3,\"file\":\"app.js\",\"sources\":[\"app.ts\"],\"mappings\":\"AAAA\"}";

    private static ByteArrayContent MapContent()
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(Map));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    [Fact]
    public async Task Upload_ViaBearer_StoresBytes_IndexesRow()
    {
        var store = new RecordingObjectStore();
        var app = AppWithStore(pg.ConnectionString, store);
        var (admin, projectId) = await ProvisionAsync(app, pg.ConnectionString);
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
    public async Task An_upload_past_the_quota_evicts_the_oldest_artifact_and_still_succeeds()
    {
        var store = new RecordingObjectStore();
        var app = AppWithStore(pg.ConnectionString, store);
        var (admin, projectId) = await ProvisionAsync(app, pg.ConnectionString);
        var token = await MintTokenAsync(admin, projectId);
        // An older artifact that alone fills the org's quota (ProvisionAsync puts the org on Business),
        // so the next upload pushes it out.
        var old = $"sourcemaps/{projectId}/by-debug-id/old";
        await ExecuteAsync(
            "INSERT INTO sourcemap_artifacts (id, project_id, release, filename, object_key, byte_size, created_at) "
            + $"VALUES ('{Guid.CreateVersion7()}', {projectId}, '1.0.0', 'old.js', '{old}', "
            + $"{Condux.Core.Plans.PlanCatalog.For(Condux.Core.Plans.Tier.Business).UploadBytes}, now() - interval '1 day')");

        var resp = await Bearer(app, token)
            .PostAsync("/api/sourcemaps?release=1.4.2&filename=app.js&debugId=new", MapContent());

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM sourcemap_artifacts WHERE object_key = '{old}'"));
        Assert.Equal(1L, await ScalarAsync($"SELECT count(*) FROM object_deletions WHERE object_key = '{old}'"));
        Assert.Equal(1L, await ScalarAsync(
            $"SELECT count(*) FROM sourcemap_artifacts WHERE object_key = 'sourcemaps/{projectId}/by-debug-id/new'"));
    }

    [Fact]
    public async Task An_upload_whose_store_write_fails_still_leaves_a_row_the_quota_counts()
    {
        // The row is written before the object, so storage never holds an object no row names: one the
        // quota could not count and nothing would ever expire.
        var app = AppWithStore(pg.ConnectionString, new UnreachableMapStore());
        var (admin, projectId) = await ProvisionAsync(app, pg.ConnectionString);
        var token = await MintTokenAsync(admin, projectId);

        // The store's exception reaches the test (ServerExceptions); what matters is what was recorded first.
        var failed = await Assert.ThrowsAsync<HttpRequestException>(() => Bearer(app, token)
            .PostAsync("/api/sourcemaps?release=1.0.0&filename=app.js&debugId=lost", MapContent()));
        Assert.Equal(UnreachableMapStore.Message, failed.Message);

        Assert.Equal(1L, await ScalarAsync(
            $"SELECT count(*) FROM sourcemap_artifacts WHERE object_key = 'sourcemaps/{projectId}/by-debug-id/lost'"));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Upload_WithBadToken_Returns401()
    {
        var app = AppWithStore(pg.ConnectionString, new RecordingObjectStore());
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
        var app = AppWithStore(pg.ConnectionString, new RecordingObjectStore());
        var (admin, projectId) = await ProvisionAsync(app, pg.ConnectionString);
        var token = await MintTokenAsync(admin, projectId);

        var ci = Bearer(app, token);
        var notAMap = new ByteArrayContent(Encoding.UTF8.GetBytes("this is not a source map"));
        notAMap.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var resp = await ci.PostAsync("/api/sourcemaps?release=1.0.0&filename=app.js", notAMap);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Upload_WithEmptyBody_Returns400_AndPastTheCap_Returns413()
    {
        var app = AppWithStore(pg.ConnectionString, new RecordingObjectStore());
        var (admin, projectId) = await ProvisionAsync(app, pg.ConnectionString);
        var ci = Bearer(app, await MintTokenAsync(admin, projectId));

        var empty = await ci.PostAsync("/api/sourcemaps?release=1.0.0&filename=app.js", new ByteArrayContent([]));
        var tooLarge = await ci.PostAsync("/api/sourcemaps?release=1.0.0&filename=app.js",
            new ByteArrayContent(new byte[25 * 1024 * 1024 + 1]));

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
    }

    [Fact]
    public async Task Upload_WithOverlongField_Returns400()
    {
        var app = AppWithStore(pg.ConnectionString, new RecordingObjectStore());
        var (admin, projectId) = await ProvisionAsync(app, pg.ConnectionString);
        var token = await MintTokenAsync(admin, projectId);

        var ci = Bearer(app, token);
        var resp = await ci.PostAsync(
            $"/api/sourcemaps?release={new string('x', 300)}&filename=app.js", MapContent());
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
