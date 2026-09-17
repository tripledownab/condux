using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Condux.Core.Auth;
using Condux.Core.Secrets;
using Condux.IntegrationTests.Fixtures;
using Condux.Storage.Postgres;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Xunit;

namespace Condux.IntegrationTests;

/// <summary>
/// The per-org enterprise-SSO OIDC config (#72): a Business/Enterprise org registers its IdP, the client
/// secret is stored encrypted (never round-tripped), and the whole surface is gated on the plan's Sso
/// feature. Mirrors <see cref="LlmConfigApiTest"/>.
///
/// Who OWNS a claimed email domain is no longer decided here. Saving a config is only a provisional claim
/// since ADR-0043, and exclusivity attaches to proving it, so those rules live in
/// <see cref="SsoDomainVerificationApiTest"/> rather than being restated on the write path.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SsoConfigApiTest(PostgresFixture pg) : IClassFixture<PostgresFixture>
{
    private static readonly string SecretKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private WebApplicationFactory<Program> CreateApp() =>
        ControlPlaneApp.Create(pg.ConnectionString).WithWebHostBuilder(b =>
            b.UseSetting("CONDUX_SECRET_KEY", SecretKey));

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
        var client = CreateApp().CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 0); // Free — no Sso

        var put = await client.PutAsJsonAsync($"/api/orgs/{orgId}/sso-config", ConfigBody("acme.test"));
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        var body = await put.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("sso_requires_upgrade", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_invalid_domain_is_rejected()
    {
        var client = CreateApp().CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 2);

        var put = await client.PutAsJsonAsync($"/api/orgs/{orgId}/sso-config", ConfigBody("no-dot"));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task A_scheme_less_endpoint_is_rejected()
    {
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
    public async Task A_saml_config_round_trips_and_clears_the_oidc_half()
    {
        var client = CreateApp().CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 2);

        // Start on OIDC, then switch the org to SAML — the stale client credentials must not linger.
        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsJsonAsync($"/api/orgs/{orgId}/sso-config", ConfigBody("saml.test"))).StatusCode);
        var put = await client.PutAsJsonAsync($"/api/orgs/{orgId}/sso-config", new
        {
            emailDomain = "saml.test",
            issuer = "https://idp.example/saml",
            protocol = 1,
            samlSsoUrl = "https://idp.example/sso",
            samlCertificate = SsoFlow.MakeCertificate().ExportCertificatePem(),
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var got = await client.GetFromJsonAsync<JsonElement>($"/api/orgs/{orgId}/sso-config");
        Assert.Equal(1, got.GetProperty("protocol").GetInt32());
        Assert.Equal("https://idp.example/sso", got.GetProperty("samlSsoUrl").GetString());
        Assert.Contains("BEGIN CERTIFICATE", got.GetProperty("samlCertificate").GetString());
        Assert.Equal(JsonValueKind.Null, got.GetProperty("clientId").ValueKind);
        Assert.Equal(JsonValueKind.Null, got.GetProperty("tokenEndpoint").ValueKind);
    }

    [Fact]
    public async Task A_saml_config_with_an_unparseable_certificate_is_rejected()
    {
        var client = CreateApp().CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 2);

        var put = await client.PutAsJsonAsync($"/api/orgs/{orgId}/sso-config", new
        {
            emailDomain = "acme.test",
            issuer = "https://idp.example/saml",
            protocol = 1,
            samlSsoUrl = "https://idp.example/sso",
            samlCertificate = "not a certificate",
        });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }


    // The escalation this refuses: an admin who could write the config would name the IdP whose
    // assertion SsoSignIn turns into a session, and that session can be the owner's, with no password
    // and no second factor. Changing a member's role is owner-only, so admin-writable SSO was a second
    // way to the same authority.
    //
    // The 403s below are load-bearing as 403s. OrgAuthorization answers 404 when the caller holds no
    // role at all and 403 only when they hold one below the minimum, so a seeding failure that left
    // this user outside the org would fail these assertions rather than pass them for the wrong reason.
    [Fact]
    public async Task An_admin_who_is_not_an_owner_cannot_write_or_delete_the_config()
    {
        var app = CreateApp();
        var owner = app.CreateClient();
        await ApiAuth.SignUpAsync(owner);
        var orgId = await CreateOrgAsync(owner, tier: 2);

        var adminClient = app.CreateClient();
        var admin = await ApiAuth.SignUpAsync(adminClient);
        await new OrgMemberRepository(pg.ConnectionString).AddAsync(orgId, admin.UserId, OrgRole.Admin);

        // Its own domain, because every test in this class shares one database and a shared name would
        // leave the assertions below reading another test's row.
        const string domain = "owner-gate.test";

        // 403 and not 404: the admin is a member, so the org's existence is not what is being hidden.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await adminClient.PutAsJsonAsync($"/api/orgs/{orgId}/sso-config", ConfigBody(domain)))
            .StatusCode);

        // The owner writes it, so the refusal above is about the role and not about the body.
        Assert.Equal(HttpStatusCode.OK,
            (await owner.PutAsJsonAsync($"/api/orgs/{orgId}/sso-config", ConfigBody(domain))).StatusCode);

        // Turning SSO off is the same authority read backwards, and a federated account has no password
        // to fall back on, so the delete is gated with the write.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await adminClient.DeleteAsync($"/api/orgs/{orgId}/sso-config")).StatusCode);

        // The admin can still READ it, which is the half that did not move.
        Assert.Equal(HttpStatusCode.OK,
            (await adminClient.GetAsync($"/api/orgs/{orgId}/sso-config")).StatusCode);
    }

    [Fact]
    public async Task The_routes_404_when_the_secret_store_is_not_configured()
    {
        // The shared app factory sets CONDUX_SECRET_KEY unconditionally, so the key has to be cleared
        // explicitly to reach the not-configured path. Without this the request 404s anyway, because
        // the org simply has no config, and the assertion below would hold with the gate deleted.
        var client = ControlPlaneApp.Create(pg.ConnectionString)
            .WithWebHostBuilder(b => b.UseSetting("CONDUX_SECRET_KEY", ""))
            .CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 2);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/orgs/{orgId}/sso-config")).StatusCode);
    }

    [Fact]
    public async Task Metadata_is_served_from_configuration_and_is_absent_when_the_feature_is_off()
    {
        var app = ControlPlaneApp.Create(pg.ConnectionString, appBaseUrl: "https://app.example.test/")
            .WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_SECRET_KEY", SecretKey);
        });
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);

        var body = await client.GetFromJsonAsync<JsonElement>("/api/auth/sso/metadata");

        // The trailing slash on the base URL must not survive: a doubled slash is a different entity id
        // to the IdP than the one without, and the assertion audience would stop matching.
        Assert.Equal("https://app.example.test/api/auth/sso/callback", body.GetProperty("redirectUri").GetString());
        Assert.Equal("https://app.example.test/api/auth/sso/saml", body.GetProperty("samlEntityId").GetString());
        Assert.Equal("https://app.example.test/api/auth/sso/saml/acs", body.GetProperty("samlAcsUrl").GetString());

        // With the key cleared the whole SSO surface is off, and this route hides with the rest.
        // Unlike the org-scoped config route, a 404 here can only mean that: it does not depend on
        // whether any org has a config, so it is the one that actually pins the gate.
        var off = ControlPlaneApp.Create(pg.ConnectionString)
            .WithWebHostBuilder(b => b.UseSetting("CONDUX_SECRET_KEY", ""))
            .CreateClient();
        await ApiAuth.SignUpAsync(off);
        Assert.Equal(HttpStatusCode.NotFound, (await off.GetAsync("/api/auth/sso/metadata")).StatusCode);
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
