using System.Net;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The dev-only dogfood lever GET /api/boom (#75): it throws so the control-plane self-reports its own
/// error to CONDUX_SELF_DSN. It never touches a store (a dummy Postgres string is enough), so it asserts
/// the throw in Development, which the test host hands to the test (ServerExceptions), and that it is not
/// mapped in production (404).
/// </summary>
[Trait("Category", "Integration")]
public sealed class DevBoomTest
{
    private const string DummyPostgres = "Host=localhost;Username=x;Password=x;Database=x";

    [Fact]
    public async Task Boom_InDevelopment_Throws()
    {
        var client = ControlPlaneApp.Create(DummyPostgres, configure: b => b.UseEnvironment("Development"))
            .CreateClient();

        var ex = await Assert.ThrowsAsync<NullReferenceException>(() => client.GetAsync("/api/boom"));

        // The server's own stack, not one rebuilt on the client side.
        Assert.Contains("Condux.ControlPlane.", ex.StackTrace);
    }

    [Fact]
    public async Task Boom_InProduction_IsNotMapped_Returns404()
    {
        var client = ControlPlaneApp.Create(DummyPostgres, configure: b => b.UseEnvironment("Production"))
            .CreateClient();

        var response = await client.GetAsync("/api/boom");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
