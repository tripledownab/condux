using System.Net;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The dev-only dogfood lever GET /api/boom (#75): it throws so the control-plane self-reports its own
/// error to CONDUX_SELF_DSN. It never touches a store (a dummy Postgres string is enough), so it asserts
/// the throw in Development (500) and that it is not mapped in production (404).
/// </summary>
[Trait("Category", "Integration")]
public sealed class DevBoomTest
{
    private const string DummyPostgres = "Host=localhost;Username=x;Password=x;Database=x";

    [Fact]
    public async Task Boom_InDevelopment_Throws_Returns500()
    {
        var client = ControlPlaneApp.Create(DummyPostgres, configure: b => b.UseEnvironment("Development"))
            .CreateClient();

        var response = await client.GetAsync("/api/boom");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
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
