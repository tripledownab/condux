using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Condux.Core.Events;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Condux.Storage.SourceMaps;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Condux.IntegrationTests.Fixtures.ReleaseTokenUploads;
using static Condux.Tests.Shared.ElfFiles;

namespace Condux.IntegrationTests;

/// <summary>
/// A Flutter obfuscation map over HTTP and back out on read: CI uploads the map naming the debug id of a
/// debug file it has already uploaded, and a crash from that build then reads with its own class names in
/// the exception type and message. Also what is refused: a map for a build with no debug file in the
/// project, a debug id that is not one, a body that is not a map, and the feature off with no object storage.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DartSymbolMapApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private const string Map = """["CartException","xY","LineItem","aB","Error","in"]""";

    [Fact]
    public async Task A_crash_from_the_build_reads_with_its_own_class_names()
    {
        var (app, ci, project) = await ConnectAsync();
        var debugId = await UploadDebugFileAsync(ci, "1a000000000000000000000000000000");

        var upload = await ci.PostAsync($"/api/dart-symbol-maps?debugId={debugId}", Json(Map));

        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var body = await upload.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("names").GetInt32());
        // The quota counts what Postgres holds for the names, not the body.
        Assert.Equal(
            DartSymbolMapRepository.ToCost("xY", "CartException") + DartSymbolMapRepository.ToCost("aB", "LineItem")
                + DartSymbolMapRepository.ToCost("in", "Error"),
            await MapCostAsync(project));
        // The event spells the id as an SDK may, upper case, which the lookup reads like the native pass does.
        var read = await SymbolicateAsync(app, project, Crash(debugId.ToUpperInvariant()));
        var ex = Assert.Single(read.Exceptions);
        Assert.Equal("CartException", ex.Type);
        Assert.Equal("Instance of 'CartException' holding Instance of 'LineItem' in checkout", ex.Value);
    }

    [Fact]
    public async Task A_map_uploaded_again_replaces_the_one_before_and_makes_the_build_newest()
    {
        var (app, ci, project) = await ConnectAsync();
        var debugId = await UploadDebugFileAsync(ci, "1f000000000000000000000000000000");
        Assert.Equal(HttpStatusCode.Created,
            (await ci.PostAsync($"/api/dart-symbol-maps?debugId={debugId}", Json(Map))).StatusCode);
        var before = await UploadedAtAsync(project);

        Assert.Equal(HttpStatusCode.Created, (await ci.PostAsync(
            $"/api/dart-symbol-maps?debugId={debugId}", Json("""["OrderException","xY"]"""))).StatusCode);

        var ex = Assert.Single((await SymbolicateAsync(app, project, Crash(debugId))).Exceptions);
        Assert.Equal("OrderException", ex.Type);
        // The second map holds no 'aB', so the first map's names are gone, not merged.
        Assert.Equal("Instance of 'OrderException' holding Instance of 'aB' in checkout", ex.Value);
        Assert.True(await UploadedAtAsync(project) > before);
    }

    [Fact]
    public async Task A_map_goes_to_the_newest_build_of_its_debug_id_only()
    {
        // Build IDs that agree in their first 16 bytes share a debug id, which an uploader can repeat as often
        // as it likes, so writing the map once per build would let it multiply the work.
        var (app, ci, project) = await ConnectAsync();
        var debugId = await UploadDebugFileAsync(ci, "20000000000000000000000000000000aa");
        Assert.Equal(debugId, await UploadDebugFileAsync(ci, "20000000000000000000000000000000bb"));

        Assert.Equal(HttpStatusCode.Created,
            (await ci.PostAsync($"/api/dart-symbol-maps?debugId={debugId}", Json(Map))).StatusCode);

        Assert.Equal(3, await NameRowsAsync(project));
        Assert.Equal("CartException",
            Assert.Single((await SymbolicateAsync(app, project, Crash(debugId))).Exceptions).Type);
    }

    [Fact]
    public void A_names_cost_counts_the_bytes_Postgres_stores_not_the_characters()
    {
        // A three-byte character is one UTF-16 unit, so counting characters charged a map of them about half
        // what Postgres holds for it.
        Assert.Equal(DartSymbolMapRepository.ToCost("xY", "Cart") + 4 * 2 * 3 + 2 * 2 * 3,
            DartSymbolMapRepository.ToCost("xY\u4e2d\u6587", "Cart\u4e2d\u6587"));
    }

    [Fact]
    public async Task A_crash_from_another_build_is_left_as_it_was()
    {
        var (app, ci, project) = await ConnectAsync();
        var mapped = await UploadDebugFileAsync(ci, "1b000000000000000000000000000000");
        var other = await UploadDebugFileAsync(ci, "1c000000000000000000000000000000");
        Assert.Equal(HttpStatusCode.Created,
            (await ci.PostAsync($"/api/dart-symbol-maps?debugId={mapped}", Json(Map))).StatusCode);

        var crash = Crash(other);
        Assert.Same(crash, await SymbolicateAsync(app, project, crash));
    }

    [Fact]
    public async Task Another_projects_map_for_the_same_build_is_not_used()
    {
        // A build ID is a value the file's author writes, so another project can upload the same one with a
        // map of its own; that map must not rename this project's classes.
        const string buildId = "1e000000000000000000000000000000";
        var (app, mine, project) = await ConnectAsync();
        var debugId = await UploadDebugFileAsync(mine, buildId);
        var (_, theirs, _) = await ConnectAsync();
        await UploadDebugFileAsync(theirs, buildId);
        Assert.Equal(HttpStatusCode.Created,
            (await theirs.PostAsync($"/api/dart-symbol-maps?debugId={debugId}", Json(Map))).StatusCode);

        var crash = Crash(debugId);
        Assert.Same(crash, await SymbolicateAsync(app, project, crash));
    }

    [Fact]
    public async Task A_map_for_a_build_with_no_debug_file_here_is_refused()
    {
        var (_, ci, _) = await ConnectAsync();

        var resp = await ci.PostAsync("/api/dart-symbol-maps?debugId=30f93e7a-2768-18da-3fa3-6f07eecef2fb", Json(Map));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("upload the build's debug file first", await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("not-a-debug-id", Map, "debugId is not a debug id")]
    [InlineData(null, """{"xY":"CartException"}""", "JSON array")]
    public async Task A_bad_debug_id_or_body_is_refused_with_the_reason(string? debugId, string body, string reason)
    {
        var (_, ci, _) = await ConnectAsync();
        debugId ??= await UploadDebugFileAsync(ci, "1d000000000000000000000000000000");

        var resp = await ci.PostAsync($"/api/dart-symbol-maps?debugId={debugId}", Json(body));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains(reason, await resp.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task With_no_object_storage_the_route_is_off()
    {
        var app = ControlPlaneApp.Create(pg.ConnectionString);
        var (admin, project) = await ProvisionAsync(app, pg.ConnectionString);
        var ci = Bearer(app, await MintTokenAsync(admin, project));

        var resp = await ci.PostAsync("/api/dart-symbol-maps?debugId=30f93e7a-2768-18da-3fa3-6f07eecef2fb", Json(Map));

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private async Task<(WebApplicationFactory<Program> App, HttpClient Ci, long Project)> ConnectAsync()
    {
        var app = AppWithStore(pg.ConnectionString, new RecordingObjectStore());
        var (admin, project) = await ProvisionAsync(app, pg.ConnectionString);
        return (app, Bearer(app, await MintTokenAsync(admin, project)), project);
    }

    private static async Task<string> UploadDebugFileAsync(HttpClient ci, string buildId)
    {
        var resp = await ci.PostAsync("/api/debug-files", new ByteArrayContent(SymbolsFile(buildId)));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("debugId").GetString()!;
    }

    private static Task<Event> SymbolicateAsync(WebApplicationFactory<Program> app, long project, Event crash) =>
        app.Services.GetRequiredService<EventSymbolication>().SymbolicateAsync(project, crash);

    private static Event Crash(string debugId) => new()
    {
        DebugImages = [new DebugImage { Type = "elf", DebugId = debugId }],
        Exceptions =
        [
            new ExceptionValue { Type = "xY", Value = "Instance of 'xY' holding Instance of 'aB' in checkout" },
        ],
    };

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<long> MapCostAsync(long project) =>
        (long)(await ScalarAsync("SELECT max(dart_symbol_map_bytes) FROM debug_files WHERE project_id = @p", project))!;

    private async Task<long> NameRowsAsync(long project) => (long)(await ScalarAsync(
        "SELECT count(*) FROM dart_symbol_names n JOIN debug_files d ON d.id = n.debug_file_id WHERE d.project_id = @p",
        project))!;

    private async Task<object?> ScalarAsync(string sql, long project)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("p", project);
        return await cmd.ExecuteScalarAsync();
    }

    private async Task<DateTimeOffset> UploadedAtAsync(long project)
    {
        await using var conn = new Npgsql.NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "SELECT created_at FROM debug_files WHERE project_id = @p", conn);
        cmd.Parameters.AddWithValue("p", project);
        return new DateTimeOffset((DateTime)(await cmd.ExecuteScalarAsync())!);
    }
}
