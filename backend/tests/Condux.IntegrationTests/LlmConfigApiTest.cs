using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Condux.ControlPlane.Llm;
using Condux.Core.Secrets;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The BYO-key registry (#65): an Enterprise org can set a validated LLM key; it is stored encrypted
/// (never round-tripped), gated on the plan's ByoKey feature, and validated live before save.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LlmConfigApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly string SecretKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private sealed class StubValidator(bool valid) : ILlmKeyValidator
    {
        public static readonly IReadOnlyList<LlmModel> Models = [new LlmModel("claude-opus-4-8", "Claude Opus 4.8")];

        public Task<IReadOnlyList<LlmModel>?> ListModelsAsync(
            string provider, string baseUrl, string apiKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(valid ? Models : null);
    }

    private WebApplicationFactory<Program> CreateApp(bool keyValid = true) =>
        ControlPlaneApp.Create(pg.ConnectionString).WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_SECRET_KEY", SecretKey);
            b.ConfigureTestServices(s => s.AddSingleton<ILlmKeyValidator>(new StubValidator(keyValid)));
        });

    // Not static: needs the connection string to set the tier out of band, since POST /api/orgs always
    // creates Free and only the Stripe webhook moves an org off it.
    private async Task<long> CreateOrgAsync(HttpClient client, int tier)
    {
        var resp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Org" });
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
        await OrgSeed.SetTierAsync(pg.ConnectionString, id, tier);
        return id;
    }

    [Fact]
    public async Task Enterprise_org_sets_reads_and_deletes_a_key_stored_encrypted()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = CreateApp().CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 3); // Enterprise has ByoKey

        const string apiKey = "sk-ant-secret-value-xyz";
        var put = await client.PutAsJsonAsync($"/api/orgs/{orgId}/llm-config",
            new { provider = "anthropic", model = "claude-opus-4-8", apiKey });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // The read returns metadata, never the key.
        var got = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/llm-config");
        Assert.Equal("anthropic", got.GetProperty("provider").GetString());
        Assert.Equal("claude-opus-4-8", got.GetProperty("model").GetString());
        Assert.False(got.TryGetProperty("apiKey", out _));
        Assert.False(got.TryGetProperty("keyEncrypted", out _));

        // The stored key is ciphertext, not plaintext, and decrypts back with the master key.
        var encrypted = await ReadEncryptedKeyAsync(orgId);
        Assert.DoesNotContain(apiKey, System.Text.Encoding.UTF8.GetString(encrypted));
        Assert.Equal(apiKey, new SecretBox(SecretKey).Open(encrypted));

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/orgs/{orgId}/llm-config")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/orgs/{orgId}/llm-config")).StatusCode);
    }

    [Fact]
    public async Task Models_can_be_listed_for_a_provided_key()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = CreateApp().CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 3);

        var resp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/llm-config/models",
            new { provider = "anthropic", apiKey = "sk-ant-x" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var models = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("models");
        Assert.Equal("claude-opus-4-8", models[0].GetProperty("id").GetString());
        Assert.Equal("Claude Opus 4.8", models[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task A_tier_without_byo_key_is_refused()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = CreateApp().CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 0); // Free — no ByoKey

        var put = await client.PutAsJsonAsync($"/api/orgs/{orgId}/llm-config",
            new { provider = "anthropic", model = "claude-opus-4-8", apiKey = "sk-ant-x" });
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("byo_key_requires_upgrade", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_invalid_key_is_rejected_before_it_is_stored()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = CreateApp(keyValid: false).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 3);

        var put = await client.PutAsJsonAsync($"/api/orgs/{orgId}/llm-config",
            new { provider = "anthropic", model = "claude-opus-4-8", apiKey = "sk-ant-bad" });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/orgs/{orgId}/llm-config")).StatusCode);
    }

    [Fact]
    public async Task The_routes_404_when_the_secret_store_is_not_configured()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        // No CONDUX_SECRET_KEY set → the feature is off.
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 3);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/orgs/{orgId}/llm-config")).StatusCode);
    }

    private async Task<byte[]> ReadEncryptedKeyAsync(long orgId)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT key_encrypted FROM llm_configs WHERE org_id = @org", conn);
        cmd.Parameters.AddWithValue("org", orgId);
        return (byte[])(await cmd.ExecuteScalarAsync())!;
    }
}
