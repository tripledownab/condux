using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Condux.Core.Secrets;
using Condux.IntegrationTests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The per-org enterprise-SSO OIDC config (#72): a Business/Enterprise org registers its IdP; the client
/// secret is stored encrypted (never round-tripped), gated on the plan's Sso feature, and the email domain
/// (the login routing key) is globally unique. Mirrors <see cref="LlmConfigApiTest"/>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SsoConfigApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly string SecretKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private WebApplicationFactory<Program> CreateApp() =>
        ControlPlaneApp.Create(pg.ConnectionString).WithWebHostBuilder(b =>
            b.UseSetting("CONDUX_SECRET_KEY", SecretKey));

    private static async Task<long> CreateOrgAsync(HttpClient client, int tier)
    {
        var resp = await client.PostAsJsonAsync("/api/orgs",
            new { slug = "org-" + Guid.NewGuid().ToString("N"), name = "Org", tier });
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }

    private static object ConfigBody(string domain) => new
    {
        emailDomain = domain,
        issuer = "https://idp.example/",
        authorizationEndpoint = "https://idp.example/authorize",
        tokenEndpoint = "https://idp.example/token",
        clientId = "client-abc",
        clientSecret = "super-secret-value-xyz",
    };

    [Fact]
    public async Task Business_org_sets_reads_and_deletes_a_config_with_the_secret_stored_encrypted()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = CreateApp().CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 2); // Business has Sso

        var put = await client.PutAsJsonAsync($"/api/orgs/{orgId}/sso-config", ConfigBody("acme.test"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // The read returns metadata, never the client secret.
        var got = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/sso-config");
        Assert.Equal("acme.test", got.GetProperty("emailDomain").GetString());
        Assert.Equal("client-abc", got.GetProperty("clientId").GetString());
        Assert.False(got.TryGetProperty("clientSecret", out _));
        Assert.False(got.TryGetProperty("clientSecretEncrypted", out _));

        // The stored secret is ciphertext, not plaintext, and decrypts back with the master key.
        var encrypted = await ReadEncryptedSecretAsync(orgId);
        Assert.DoesNotContain("super-secret-value-xyz", Encoding.UTF8.GetString(encrypted));
        Assert.Equal("super-secret-value-xyz", new SecretBox(SecretKey).Open(encrypted));

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/orgs/{orgId}/sso-config")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/orgs/{orgId}/sso-config")).StatusCode);
    }

    [Fact]
    public async Task A_tier_without_sso_is_refused()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = CreateApp().CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 0); // Free — no Sso

        var put = await client.PutAsJsonAsync($"/api/orgs/{orgId}/sso-config", ConfigBody("acme.test"));
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sso_requires_upgrade", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_domain_owned_by_another_org_is_refused()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var app = CreateApp();

        var clientA = app.CreateClient();
        await ApiAuth.SignUpAsync(clientA);
        var orgA = await CreateOrgAsync(clientA, tier: 2);
        Assert.Equal(HttpStatusCode.OK,
            (await clientA.PutAsJsonAsync($"/api/orgs/{orgA}/sso-config", ConfigBody("shared.test"))).StatusCode);

        // A second org (different owner — one org per user) can't claim the same domain.
        var clientB = app.CreateClient();
        await ApiAuth.SignUpAsync(clientB);
        var orgB = await CreateOrgAsync(clientB, tier: 2);
        var put = await clientB.PutAsJsonAsync($"/api/orgs/{orgB}/sso-config", ConfigBody("shared.test"));
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal("email_domain_taken",
            (await put.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_invalid_domain_is_rejected()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = CreateApp().CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 2);

        var put = await client.PutAsJsonAsync($"/api/orgs/{orgId}/sso-config", ConfigBody("no-dot"));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task A_scheme_less_endpoint_is_rejected()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        var client = CreateApp().CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 2);

        var put = await client.PutAsJsonAsync($"/api/orgs/{orgId}/sso-config", new
        {
            emailDomain = "acme.test",
            issuer = "https://idp.example/",
            authorizationEndpoint = "idp.example/authorize", // no scheme
            tokenEndpoint = "https://idp.example/token",
            clientId = "client-abc",
            clientSecret = "super-secret-value-xyz",
        });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task The_routes_404_when_the_secret_store_is_not_configured()
    {
        await Migrations.ApplyAllAsync(pg.ConnectionString);
        // No CONDUX_SECRET_KEY → the feature is off.
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 2);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/orgs/{orgId}/sso-config")).StatusCode);
    }

    private async Task<byte[]> ReadEncryptedSecretAsync(long orgId)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT client_secret_encrypted FROM sso_configs WHERE org_id = @org", conn);
        cmd.Parameters.AddWithValue("org", orgId);
        return (byte[])(await cmd.ExecuteScalarAsync())!;
    }
}
