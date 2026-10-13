using System.Net.Http.Json;
using System.Text.Json;
using Condux.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The live stream ends when the host starts stopping. The server waits for open requests before it disposes
/// its services, so a stream that waited for the client instead would hold shutdown until the host's timeout
/// and the job producers' flush would never run inside the container's grace period.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProjectEventStreamShutdownTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static async Task<(HttpClient Client, long ProjectId)> ProvisionAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs", new { name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { name = "Backend", platform = "javascript" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
        return (client, projectId);
    }

    [Fact]
    public async Task An_open_stream_ends_when_the_host_starts_stopping()
    {
        await using var factory = ControlPlaneApp.Create(pg.ConnectionString);
        var (client, projectId) = await ProvisionAsync(factory);

        using var response = await client.GetAsync(
            $"/api/projects/{projectId}/events", HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        Assert.Equal(": connected", await reader.ReadLineAsync());

        factory.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        // Shorter than the stream's own 20s heartbeat, so a stream still open cannot pass by chance.
        var rest = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("ping", rest);
    }
}
