extern alias relay;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.Messaging;
using Condux.Core.Projects;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// End-to-end DSN auth at the relay against real Postgres (#42). Mints a DSN through the
/// control-plane provisioning API, then hosts the relay with <c>CONDUX_POSTGRES</c> set
/// (exactly how compose runs it: <c>CachingProjectStore → PostgresProjectStore</c>) and proves
/// the minted DSN is accepted at the ingest endpoint (<c>202</c>) while a wrong key is rejected
/// (<c>401</c>). This closes the gap that <see cref="CatalogProvisioningTest"/> (store-level) and
/// the relay's in-memory <c>IngestEndpointTests</c> each cover only half of. Opt-in via
/// <c>--filter Category=Integration</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RelayPostgresAuthTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    // Host the control-plane (global Program) — mints DSNs against the shared Postgres.
    private HttpClient ControlPlane() => ControlPlaneApp.Create(pg.ConnectionString).CreateClient();

    // Host the relay (relay::Program) with the Postgres-backed project store, plus an
    // in-memory publisher so no broker is needed to observe what got accepted.
    private WebApplicationFactory<relay::Program> Relay(IEventPublisher publisher) =>
        new WebApplicationFactory<relay::Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_POSTGRES", pg.ConnectionString);
            b.ConfigureServices(s => s.AddSingleton(publisher));
        });

    [Fact]
    public async Task ApiMintedDsn_AuthenticatesAtRelay_WrongKeyRejected()
    {
        var api = ControlPlane();
        await ApiAuth.SignUpAsync(api); // authenticate; the caller owns the org it creates

        // Mint a DSN the way a user would: create an org, then a project (which returns
        // a ready-to-use DSN string).
        var orgResp = await api.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        Assert.Equal(HttpStatusCode.Created, orgResp.StatusCode);
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, orgId, 1);

        var projResp = await api.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { name = "Backend", platform = "python" });
        Assert.Equal(HttpStatusCode.Created, projResp.StatusCode);
        var projBody = await projResp.Content.ReadFromJsonAsync<JsonElement>();
        var dsn = projBody.GetProperty("dsn").GetString();
        var numericId = projBody.GetProperty("project").GetProperty("id").GetInt64()
            .ToString(CultureInfo.InvariantCulture);
        var publicId = projBody.GetProperty("project").GetProperty("publicId").GetString();

        // The exact DSN string a user copies carries the project's public UUID (#126) — never the bigint.
        Assert.True(Dsn.TryParse(dsn!, out var parsed));
        Assert.Equal(publicId, parsed!.ProjectId);

        var pub = new InMemoryEventPublisher();
        var relay = Relay(pub).CreateClient();

        // The minted (UUID) DSN authenticates → the event is accepted and published once...
        var ok = new HttpRequestMessage(HttpMethod.Post, $"/api/{parsed.ProjectId}/store/")
        {
            Content = new StringContent("""{"message":"boom","level":"error"}"""),
        };
        ok.Headers.Add("x-condux-auth", parsed.PublicKey);
        Assert.Equal(HttpStatusCode.OK, (await relay.SendAsync(ok)).StatusCode);
        var published = Assert.Single(pub.Published);
        // ...but the relay resolves the UUID to the numeric id for the Kafka key, so downstream stays numeric.
        Assert.Equal(numericId, published.ProjectId);

        // A wrong key for the same project is rejected before anything is published.
        var bad = new HttpRequestMessage(HttpMethod.Post, $"/api/{parsed.ProjectId}/store/")
        {
            Content = new StringContent("{}"),
        };
        bad.Headers.Add("x-condux-auth", "not-the-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await relay.SendAsync(bad)).StatusCode);
        Assert.Single(pub.Published); // still just the one accepted event
    }
}
