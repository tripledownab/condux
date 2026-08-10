using System.Net.Http;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// Dev CORS for the split-origin dashboard (web :3000 → API :8080). Runs over the in-memory TestServer
/// (no Docker, no real ports) - a preflight only touches the CORS middleware, never a datastore, so a
/// placeholder connection string is enough. Marked Integration to sit with the other WebApplicationFactory
/// tests; it needs no containers, so it is safe to run with the compose stack up.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CorsTest
{
    private const string DummyPostgres = "Host=localhost;Username=x;Password=x;Database=x";
    private const string Origin = "http://localhost:3000";

    private static HttpRequestMessage Preflight()
    {
        var req = new HttpRequestMessage(HttpMethod.Options, "/api/auth/login");
        req.Headers.Add("Origin", Origin);
        req.Headers.Add("Access-Control-Request-Method", "POST");
        return req;
    }

    [Fact]
    public async Task Preflight_from_a_configured_origin_is_allowed_with_credentials()
    {
        using var app = ControlPlaneApp.Create(DummyPostgres)
            .WithWebHostBuilder(b => b.UseSetting("CONDUX_CORS_ORIGINS", Origin));

        var resp = await app.CreateClient().SendAsync(Preflight());

        Assert.Equal(Origin, resp.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", resp.Headers.GetValues("Access-Control-Allow-Credentials").Single());
    }

    [Fact]
    public async Task Without_configured_origins_no_cors_headers_are_emitted()
    {
        using var app = ControlPlaneApp.Create(DummyPostgres); // CONDUX_CORS_ORIGINS unset (prod default)

        var resp = await app.CreateClient().SendAsync(Preflight());

        Assert.False(resp.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
