using Condux.Core.DebugFiles;
using System.Net;
using System.Net.Http.Json;
using Condux.Symbolizer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.Symbolizer.Tests;

/// <summary>
/// The service around a parser that fails, which the real llvm-symbolizer cannot be made to do on demand: a
/// failed run answers 502 rather than an empty success, and a parser that does not work stops the service
/// at startup. A fake stands in, so these run without LLVM installed.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class SymbolizerFailureApiTests : IDisposable
{
    private readonly FakeSymbolizers _fakes = new();

    public void Dispose() => _fakes.Dispose();

    private WebApplicationFactory<Program> App(string binary) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_LLVM_SYMBOLIZER", binary);
            b.UseSetting("CONDUX_SYMBOLIZER_CACHE_DIR", Path.Combine(_fakes.Directory, "cache"));
        });

    private static readonly byte[] FixtureBytes =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cart.so"));

    private static readonly string FixtureHash = DebugFileHashes.ToSha256(FixtureBytes);

    private static ByteArrayContent Fixture() => new(FixtureBytes);

    [Fact]
    public async Task A_run_that_fails_answers_bad_gateway_not_an_empty_success()
    {
        // Answers the startup check, then fails every batch.
        using var app = App(_fakes.Write("""[ "$1" = --version ] && exit 0; cat > /dev/null; exit 1"""));
        var client = app.CreateClient();
        await client.PutAsync($"/files/{FixtureHash}", Fixture());

        var response = await client.PostAsJsonAsync("/resolve", new ResolveRequest(FixtureHash, ["0x4600"]));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task An_empty_batch_is_refused_even_for_a_held_build()
    {
        using var app = App(_fakes.Write("""[ "$1" = --version ] && exit 0; cat > /dev/null"""));
        var client = app.CreateClient();
        await client.PutAsync($"/files/{FixtureHash}", Fixture());

        var response = await client.PostAsJsonAsync("/resolve", new ResolveRequest(FixtureHash, []));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public void A_parser_that_does_not_work_stops_the_service_at_startup()
    {
        using var app = App(_fakes.Write("exit 1"));

        var refused = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("CONDUX_LLVM_SYMBOLIZER", refused.Message);
    }

    [Fact]
    public void A_parser_that_hangs_at_startup_stops_the_service_rather_than_holding_it()
    {
        using var app = App(_fakes.Write("exec sleep 300"));

        var refused = Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        Assert.Contains("did not finish", refused.Message);
    }

    [Fact]
    public async Task An_upload_past_the_places_for_files_is_turned_away_to_try_later()
    {
        using var app = App(_fakes.Write("""[ "$1" = --version ] && exit 0; cat > /dev/null"""));
        var client = app.CreateClient();
        var gate = app.Services.GetRequiredService<FileUploadGate>();
        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());

        var response = await client.PutAsync($"/files/{FixtureHash}", Fixture());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task A_resolve_past_the_places_for_runs_is_turned_away_to_try_later()
    {
        using var app = App(_fakes.Write("""[ "$1" = --version ] && exit 0; while read a; do echo '{}'; done"""));
        var client = app.CreateClient();
        await client.PutAsync($"/files/{FixtureHash}", Fixture());
        var gate = app.Services.GetRequiredService<ResolveGate>();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(gate.TryEnter());
        }

        var busy = await client.PostAsJsonAsync("/resolve", new ResolveRequest(FixtureHash, ["0x10"]));
        gate.Leave();
        var free = await client.PostAsJsonAsync("/resolve", new ResolveRequest(FixtureHash, ["0x10"]));

        Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
        Assert.Equal(HttpStatusCode.OK, free.StatusCode);
    }
}
