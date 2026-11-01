using System.Net;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The published OpenAPI document names exactly one server, the relative "/", so a client resolves the API
/// against wherever it fetched the document (OAS 3.0.4, the Server Object's url). Explicit rather than
/// absent because openapi-generator and Kiota resolve an explicit "/" that way and fall back to
/// http://localhost, or to asking, when the list is absent, though the spec makes the two equivalent.
///
/// <para>Runs over the plain-http TestServer with an https base URL declared, which is production's
/// shape: TLS ends at the edge, so the request the generator sees is http. Left to itself the generator
/// writes that request's scheme and host into <c>servers</c>, so a deployment behind such a proxy advertises
/// http, and here it would write <c>http://localhost</c>.</para>
///
/// <para>The document served must also match the committed <c>contracts/openapi.json</c>, line endings aside, which
/// the TS client and the Postman collection are generated from. CI regenerates those two from the committed
/// file, so without this comparison an endpoint change or a bump of the OpenAPI library that alters the
/// document passes every gate and surfaces as drift at someone's next regeneration.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class OpenApiDocumentApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task TheDocumentAdvertisesOnlyTheRelativeServer()
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString,
            configure: b => b.UseSetting("CONDUX_APP_BASE_URL", "https://app.example.test")).CreateClient();

        var resp = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        // Exactly "/": an absolute URL is the defect, and so is an absent list, for the generators above.
        var urls = doc.RootElement.GetProperty("servers").EnumerateArray()
            .Select(server => server.GetProperty("url").GetString())
            .ToList();
        Assert.Equal(["/"], urls);
        Assert.Equal("Condux Control Plane API", doc.RootElement.GetProperty("info").GetProperty("title").GetString());
    }

    [Fact]
    public async Task TheServedDocumentIsTheCommittedContract()
    {
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

        var served = ToComparable(await client.GetStringAsync("/openapi/v1.json"));
        var committed = ToComparable(
            await File.ReadAllTextAsync(Path.Combine(RepoFiles.FindRoot(), "contracts", "openapi.json")));
        if (served == committed)
        {
            return;
        }

        var at = Enumerable.Range(0, Math.Min(served.Length, committed.Length))
            .FirstOrDefault(i => served[i] != committed[i], Math.Min(served.Length, committed.Length));
        Assert.Fail(
            $"contracts/openapi.json is not what the control-plane serves; they first differ at character {at}.\n"
            + $"committed: ...{Around(committed, at)}...\nserved:    ...{Around(served, at)}...\n"
            + "Re-export it (curl /openapi/v1.json from a running control-plane), then run `pnpm gen:api` in "
            + "web/ and `pnpm gen:postman` at the root, and commit all three.");
    }

    // An editor or a Windows checkout may add a final newline or carriage returns to the committed file;
    // neither changes the contract, so neither may fail the comparison.
    private static string ToComparable(string document) => document.Replace("\r\n", "\n").TrimEnd('\r', '\n');

    private static string Around(string text, int at) => text[Math.Max(0, at - 60)..Math.Min(text.Length, at + 60)];
}
