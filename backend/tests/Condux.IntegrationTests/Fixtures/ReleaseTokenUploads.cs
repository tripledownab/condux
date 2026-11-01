using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Condux.Core.SourceMaps;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Condux.IntegrationTests.Fixtures;

/// <summary>The setup every CI upload test shares: a control-plane with object storage switched on but
/// backed by memory, a project to upload into, a release token for it, and a client carrying only that
/// token. Shared by the source map and debug file upload tests, which authenticate the same way.</summary>
public static class ReleaseTokenUploads
{
    // Object storage "configured" so the routes are live, with the registration overridden by a store in
    // memory, so no test touches an S3 server.
    public static WebApplicationFactory<Program> AppWithStore(string postgres, IObjectStore store) =>
        ControlPlaneApp.Create(postgres, configure: b =>
        {
            b.UseSetting("CONDUX_S3_ENDPOINT", "http://s3.invalid:9000");
            b.UseSetting("CONDUX_S3_BUCKET", "test-bucket");
            b.UseSetting("CONDUX_S3_ACCESS_KEY", "test");
            b.UseSetting("CONDUX_S3_SECRET_KEY", "test");
            b.ConfigureTestServices(s => s.AddSingleton(store));
        });

    // The tier is set out of band, since POST /api/orgs always creates Free and only the Stripe webhook
    // moves an org off it.
    public static async Task<(HttpClient Admin, long ProjectId)> ProvisionAsync(
        WebApplicationFactory<Program> app, string postgres)
    {
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgResp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "acme-" + Guid.NewGuid().ToString("N"), name = "Acme" });
        var orgId = (await orgResp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(postgres, orgId, 2);
        var projResp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/projects",
            new { slug = "web", name = "Web", platform = "javascript" });
        var projectId = (await projResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project").GetProperty("id").GetInt64();
        return (client, projectId);
    }

    public static async Task<string> MintTokenAsync(HttpClient admin, long projectId)
    {
        var mint = await admin.PostAsJsonAsync($"/api/projects/{projectId}/release-tokens", new { name = "CI" });
        return (await mint.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    public static HttpClient Bearer(WebApplicationFactory<Program> app, string token)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}

/// <summary>An object store that keeps whatever is put into it, so a test can read back what an upload stored.</summary>
public sealed class RecordingObjectStore : IObjectStore
{
    public Dictionary<string, byte[]> Objects { get; } = new();

    /// <summary>Runs inside each put, before the bytes are stored: something that happens while an upload is
    /// still storing its file.</summary>
    public Func<Task>? DuringPut { get; set; }

    public async Task PutAsync(
        string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken = default)
    {
        if (DuringPut is { } duringPut)
        {
            await duringPut();
        }
        Objects[key] = content.ToArray();
    }

    public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Objects.TryGetValue(key, out var value) ? value : null);
}
