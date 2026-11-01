using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.ControlPlane.Endpoints;
using Condux.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Condux.IntegrationTests.Fixtures.ReleaseTokenUploads;
using static Condux.Tests.Shared.ElfFiles;
using MachO = Condux.Tests.Shared.MachOFiles;

namespace Condux.IntegrationTests;

/// <summary>
/// Native debug file upload over HTTP: CI posts a symbols file (Android) or a dSYM's DWARF file (iOS) with
/// only a release token, and the file is stored and indexed under the build read from the file itself, a
/// universal dSYM as one file per architecture. Also covers what is refused: a stripped library or a shipped
/// binary carrying the same build, a body that is neither ELF nor Mach-O, a body past the cap, a bad token,
/// and the feature switched off when object storage is not configured.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DebugFileApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string BuildId = "7a3ef9306827da183fa36f07eecef2fb";
    private const string DebugId = "30f93e7a-2768-18da-3fa3-6f07eecef2fb";

    [Fact]
    public async Task A_symbols_file_is_stored_under_its_own_build_id()
    {
        var store = new RecordingObjectStore();
        var (ci, projectId) = await ConnectAsync(store);
        var file = SymbolsFile(BuildId);

        var resp = await ci.PostAsync("/api/debug-files", new ByteArrayContent(file));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(BuildId, body.GetProperty("buildId").GetString());
        Assert.Equal(DebugId, body.GetProperty("debugId").GetString());
        Assert.Equal(file.Length, body.GetProperty("byteSize").GetInt64());
        var stored = Assert.Single(store.Objects);
        Assert.Equal($"debug-files/{projectId}/{BuildId}", stored.Key);
        Assert.Equal(file, stored.Value);
        Assert.Equal([(BuildId, DebugId)], await RowsAsync(projectId));
    }

    [Fact]
    public async Task A_dsym_is_stored_under_its_own_uuid()
    {
        var store = new RecordingObjectStore();
        var (ci, projectId) = await ConnectAsync(store);
        // The arm64 slice of a real Flutter macOS build's dSYM; its UUID is the debug_id the crash carried.
        var dsym = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Golden", "flutter-probe-macos-arm64.dwarf"));

        var resp = await ci.PostAsync("/api/debug-files", new ByteArrayContent(dsym));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        const string uuid = "e191a89de2c230c4bf831d9222717212";
        Assert.Equal(uuid, body.GetProperty("buildId").GetString());
        Assert.Equal("e191a89d-e2c2-30c4-bf83-1d9222717212", body.GetProperty("debugId").GetString());
        var only = Assert.Single(body.GetProperty("files").EnumerateArray());
        Assert.Equal("arm64", only.GetProperty("architecture").GetString());
        Assert.Equal(dsym, Assert.Single(store.Objects).Value);
        Assert.Equal([(uuid, "e191a89d-e2c2-30c4-bf83-1d9222717212")], await RowsAsync(projectId));
    }

    [Fact]
    public async Task A_universal_dsym_is_stored_as_one_file_per_architecture()
    {
        var store = new RecordingObjectStore();
        var (ci, projectId) = await ConnectAsync(store);
        var intel = MachO.Dsym("C4905206-86BE-3B69-9564-66EEDAD3CAEB", MachO.X86_64);
        var arm = MachO.Dsym("E191A89D-E2C2-30C4-BF83-1D9222717212", MachO.Arm64);

        var resp = await ci.PostAsync("/api/debug-files", new ByteArrayContent(MachO.Universal(false, intel, arm)));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var files = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(["x86_64", "arm64"], files.Select(f => f.GetProperty("architecture").GetString()));
        // Each architecture's object is its thin file, which is what llvm-symbolizer reads without being told
        // which architecture to take.
        Assert.Equal(intel, store.Objects[$"debug-files/{projectId}/c490520686be3b69956466eedad3caeb"]);
        Assert.Equal(arm, store.Objects[$"debug-files/{projectId}/e191a89de2c230c4bf831d9222717212"]);
        Assert.Equal(2, (await RowsAsync(projectId)).Count);
    }

    [Fact]
    public async Task The_app_binary_that_shipped_is_refused_and_pointed_at_its_dsym()
    {
        var store = new RecordingObjectStore();
        var (ci, projectId) = await ConnectAsync(store);
        var binary = MachO.Thin(MachO.Arm64, MachO.UuidCommand("E191A89D-E2C2-30C4-BF83-1D9222717212"),
            MachO.Segment("__TEXT", ("__text", 64)));

        var resp = await ci.PostAsync("/api/debug-files", new ByteArrayContent(binary));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("dSYM", await resp.Content.ReadAsStringAsync());
        Assert.Empty(store.Objects);
        Assert.Empty(await RowsAsync(projectId));
    }

    [Fact]
    public async Task An_upload_past_the_quota_evicts_the_oldest_artifact_and_still_succeeds()
    {
        var (ci, projectId) = await ConnectAsync(new RecordingObjectStore());
        // Fills the org's quota on the tier ProvisionAsync gives it (Business), so the upload pushes it out.
        var old = $"debug-files/{projectId}/old";
        await using (var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "INSERT INTO debug_files (id, project_id, build_id, debug_id, object_key, byte_size, created_at) "
                + "VALUES (@id, @p, 'old', 'old', @key, @size, now() - interval '1 day')", conn);
            cmd.Parameters.AddWithValue("id", Guid.CreateVersion7());
            cmd.Parameters.AddWithValue("p", projectId);
            cmd.Parameters.AddWithValue("key", old);
            cmd.Parameters.AddWithValue("size", Condux.Core.Plans.PlanCatalog.For(Condux.Core.Plans.Tier.Business).UploadBytes);
            await cmd.ExecuteNonQueryAsync();
        }

        var resp = await ci.PostAsync("/api/debug-files", new ByteArrayContent(SymbolsFile(BuildId)));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Equal([(BuildId, DebugId)], await RowsAsync(projectId));
    }

    [Fact]
    public async Task An_upload_whose_store_write_fails_still_leaves_a_row_the_quota_counts()
    {
        // The row is written before the object, so storage never holds an object no row names.
        var (ci, projectId) = await ConnectAsync(new UnreachableMapStore());

        // The store's exception reaches the test (ServerExceptions); what matters is what was recorded first.
        var failed = await Assert.ThrowsAsync<HttpRequestException>(
            () => ci.PostAsync("/api/debug-files", new ByteArrayContent(SymbolsFile(BuildId))));
        Assert.Equal(UnreachableMapStore.Message, failed.Message);

        Assert.Equal([(BuildId, DebugId)], await RowsAsync(projectId));
        // And no hash: the hash names the bytes the bucket holds, which here are none, or on a re-upload
        // the previous upload's. A row without one is hashed from the stored file when it is first read.
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand("SELECT sha256 FROM debug_files WHERE project_id = @p", conn);
        cmd.Parameters.AddWithValue("p", projectId);
        Assert.Equal(DBNull.Value, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Uploading_the_same_build_again_keeps_one_row()
    {
        var (ci, projectId) = await ConnectAsync(new RecordingObjectStore());

        await ci.PostAsync("/api/debug-files", new ByteArrayContent(SymbolsFile(BuildId)));
        var again = await ci.PostAsync("/api/debug-files", new ByteArrayContent(SymbolsFile(BuildId, wide: false)));

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Single(await RowsAsync(projectId));
    }

    [Fact]
    public async Task The_stripped_library_that_shipped_is_refused_and_nothing_is_stored()
    {
        var store = new RecordingObjectStore();
        var (ci, projectId) = await ConnectAsync(store);
        var stripped = Elf(true, BuildIdNote(BuildId), new Section(".debug_info", NoBits, [], 4096));

        var resp = await ci.PostAsync("/api/debug-files", new ByteArrayContent(stripped));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("stripped library", await resp.Content.ReadAsStringAsync());
        Assert.Empty(store.Objects);
        Assert.Empty(await RowsAsync(projectId));
    }

    [Fact]
    public async Task A_body_that_is_neither_elf_nor_mach_o_is_refused()
    {
        var (ci, _) = await ConnectAsync(new RecordingObjectStore());

        var resp = await ci.PostAsync("/api/debug-files", new StringContent("{\"version\":3}"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("neither ELF nor Mach-O", await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_body_past_the_cap_is_refused_before_it_is_stored()
    {
        var store = new RecordingObjectStore();
        var (ci, _) = await ConnectAsync(store);
        // A valid symbols file grown past the cap, so the size alone is what can refuse it.
        var file = Elf(true, BuildIdNote(BuildId), Line(), new Section(".debug_info", Progbits, new byte[64 * 1024 * 1024]));

        var resp = await ci.PostAsync("/api/debug-files", new ByteArrayContent(file));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, resp.StatusCode);
        Assert.Empty(store.Objects);
    }

    [Fact]
    public async Task A_bad_token_is_refused()
    {
        var app = AppWithStore(pg.ConnectionString, new RecordingObjectStore());

        var resp = await Bearer(app, "condux_rel_nope")
            .PostAsync("/api/debug-files", new ByteArrayContent(SymbolsFile(BuildId)));

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task The_route_is_absent_without_object_storage()
    {
        var app = ControlPlaneApp.Create(pg.ConnectionString);

        var resp = await app.CreateClient().PostAsync("/api/debug-files", new ByteArrayContent(SymbolsFile(BuildId)));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private async Task<(HttpClient Ci, long ProjectId)> ConnectAsync(Condux.Core.SourceMaps.IObjectStore store)
    {
        var app = AppWithStore(pg.ConnectionString, store);
        var (admin, projectId) = await ProvisionAsync(app, pg.ConnectionString);
        return (Bearer(app, await MintTokenAsync(admin, projectId)), projectId);
    }

    private async Task<List<(string BuildId, string DebugId)>> RowsAsync(long projectId)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT build_id, debug_id FROM debug_files WHERE project_id = @p", conn);
        cmd.Parameters.AddWithValue("p", projectId);
        var rows = new List<(string, string)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }
        return rows;
    }
}
