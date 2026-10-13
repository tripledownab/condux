using Condux.Tests.Shared;
using Condux.Core.DebugFiles;
using System.Net;
using System.Net.Http.Json;
using Condux.Symbolizer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using static Condux.Tests.Shared.ElfFiles;

namespace Condux.Symbolizer.Tests;

/// <summary>
/// The service end to end against the real llvm-symbolizer and a real DWARF 2 fixture built by the NDK
/// (Fixtures/README.md): hand over the file, resolve the trap address to its inline chain, and survive a
/// file whose DWARF is garbage. Needs the binary, so it runs with the integration tests, and it fails rather
/// than skips without one, since a skipped test reads exactly like a passing one.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SymbolizerApiTest : IDisposable
{
    // The address of the trap inside the inlined apply_discount.
    private const string TrapAddress = "0x4600";

    private readonly string _cache = Path.Combine(Path.GetTempPath(), "symbolizer-api-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _app;

    public SymbolizerApiTest()
    {
        var binary = SymbolizerBinary.Locate();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_LLVM_SYMBOLIZER", binary);
            b.UseSetting("CONDUX_SYMBOLIZER_CACHE_DIR", _cache);
        });
    }

    public void Dispose()
    {
        _app.Dispose();
        if (Directory.Exists(_cache))
        {
            Directory.Delete(_cache, recursive: true);
        }
    }

    private static readonly byte[] FixtureBytes =
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cart.so"));

    private static readonly string FixtureHash = DebugFileHashes.ToSha256(FixtureBytes);

    private static ByteArrayContent Fixture() => new(FixtureBytes);

    [Fact]
    public async Task The_trap_resolves_to_the_inlined_function_and_the_function_it_was_inlined_into()
    {
        var client = _app.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/files/{FixtureHash}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"/files/{FixtureHash}", Fixture())).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"/files/{FixtureHash}")).StatusCode);

        var response = await client.PostAsJsonAsync("/resolve", new ResolveRequest(FixtureHash, [TrapAddress, "0x0"]));
        var body = await response.Content.ReadFromJsonAsync<ResolveResponse>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TrapAddress, body!.Results[0].Address);
        Assert.Equal(
            [new ResolvedFrame("apply_discount", "/src/cart.c", 5, 5), new ResolvedFrame("cart_total", "/src/cart.c", 11, 10)],
            body.Results[0].Frames);
        Assert.Empty(body.Results[1].Frames);
    }

    [Fact]
    public async Task A_file_whose_dwarf_is_garbage_costs_its_own_batch_and_nothing_else()
    {
        var client = _app.CreateClient();
        // Shaped like a symbols file, so it is stored, with DWARF sections full of noise.
        var noise = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7919 % 251)).ToArray();
        var hostile = Elf(true, BuildIdNote("0123456789abcdef0123456789abcdef"),
            new Section(".debug_info", Progbits, noise), new Section(".debug_line", Progbits, noise));
        var hostileHash = DebugFileHashes.ToSha256(hostile);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"/files/{hostileHash}", new ByteArrayContent(hostile))).StatusCode);

        var hostileAnswer = await client.PostAsJsonAsync("/resolve", new ResolveRequest(hostileHash, ["0x10"]));
        await client.PutAsync($"/files/{FixtureHash}", Fixture());
        var afterwards = await client.PostAsJsonAsync("/resolve", new ResolveRequest(FixtureHash, [TrapAddress]));

        Assert.True(hostileAnswer.StatusCode is HttpStatusCode.OK or HttpStatusCode.BadGateway, $"{hostileAnswer.StatusCode}");
        Assert.Equal(HttpStatusCode.OK, afterwards.StatusCode);
    }

    [Fact]
    public async Task A_file_sent_under_another_hash_is_refused()
    {
        var response = await _app.CreateClient().PutAsync($"/files/{new string('0', 64)}", Fixture());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-hash", "0x10")]
    // Hex digits with no prefix: refused for the missing prefix, not because they fail to parse.
    [InlineData(null, "1234")]
    [InlineData(null, "0xnope")]
    public async Task A_malformed_request_is_refused(string? file, string address)
    {
        var response = await _app.CreateClient().PostAsJsonAsync("/resolve", new ResolveRequest(file ?? FixtureHash, [address]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_batch_past_the_address_cap_is_refused()
    {
        var addresses = Enumerable.Range(0, 513).Select(i => $"0x{i:x}").ToList();

        var response = await _app.CreateClient().PostAsJsonAsync("/resolve", new ResolveRequest(FixtureHash, addresses));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_file_that_is_not_held_is_not_found()
    {
        var response = await _app.CreateClient().PostAsJsonAsync("/resolve", new ResolveRequest(FixtureHash, [TrapAddress]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // A field the body leaves out, or sends as null, binds as null whatever the record declares. HASH
    // stands in for the fixture's hash, which is not a constant.
    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("not json")]
    [InlineData("{\"file\":null,\"addresses\":[\"0x10\"]}")]
    [InlineData("{\"file\":\"HASH\"}")]
    [InlineData("{\"file\":\"HASH\",\"addresses\":[null]}")]
    [InlineData("{\"file\":\"HASH\\n\",\"addresses\":[\"0x10\"]}")]
    public async Task A_body_missing_a_field_is_refused_rather_than_failing(string json)
    {
        var response = await _app.CreateClient().PostAsync("/resolve",
            new StringContent(json.Replace("HASH", FixtureHash), System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_batch_body_past_its_cap_is_refused_before_it_is_parsed()
    {
        var padding = new string(' ', SymbolizerEndpoints.MaxResolveBodyBytes);
        var json = $"{{\"file\":\"{FixtureHash}\",\"addresses\":[\"0x1\"]{padding}}}";

        var response = await _app.CreateClient().PostAsync("/resolve",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }
}
