using System.Net;
using Condux.ControlPlane.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The challenge middleware fills an absent WWW-Authenticate and never replaces one a route wrote. A route
/// that needs parameters of its own, such as an OAuth <c>resource_metadata</c>, would otherwise lose them
/// silently, since the middleware's callback runs after the route's. No database: a bare app is enough.
/// </summary>
public sealed class AuthChallengeMiddlewareTest
{
    [Fact]
    public async Task ARoutesOwnChallengeSurvives_AndAnAbsentOneIsFilled()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.UseAuthChallenge();
        app.MapGet("/own", (HttpContext http) =>
        {
            http.Response.Headers.WWWAuthenticate = "Bearer resource_metadata=\"https://example.test/meta\"";
            return Results.Unauthorized();
        });
        app.MapGet("/none", () => Results.Unauthorized());
        await app.StartAsync();
        var client = app.GetTestClient();

        var own = await client.GetAsync("/own");
        Assert.Equal(HttpStatusCode.Unauthorized, own.StatusCode);
        Assert.Equal("resource_metadata=\"https://example.test/meta\"",
            Assert.Single(own.Headers.WwwAuthenticate).Parameter);

        var none = await client.GetAsync("/none");
        Assert.Equal("Session", Assert.Single(none.Headers.WwwAuthenticate).Scheme);
    }
}
