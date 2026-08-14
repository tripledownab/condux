using System.Net;
using System.Text.Json;
using Condux.Agent.ManagedAgents;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Condux.Conductor.Tests;

/// <summary>
/// The thin Managed Agents HTTP client (ADR-0038): every call carries the beta + key headers, wire
/// shapes match what the live beta returns (captured 2026-08-14), errors surface the vendor message
/// with the status so the gateway can tell a 429 from a failure, and dollars convert to the wire's
/// integer minor-unit amounts. Stub HTTP only — CI never calls the real vendor.
/// </summary>
public sealed class ManagedAgentsClientTests
{

    private static (ManagedAgentsClient Client, StubHttpHandler Http) Create()
    {
        var handler = new StubHttpHandler();
        var client = new ManagedAgentsClient(
            new HttpClient(handler), new ManagedAgentsOptions("test-key", 5m));
        return (client, handler);
    }

    [Fact]
    public async Task Every_call_carries_the_beta_and_key_headers()
    {
        var (client, http) = Create();
        http.Routes["GET /v1/agents"] = (HttpStatusCode.OK, """{"data":[]}""");

        await client.ListAgentsAsync();

        var request = Assert.Single(http.Requests);
        Assert.Equal("managed-agents-2026-04-01", request.Headers["anthropic-beta"]);
        Assert.Equal("test-key", request.Headers["x-api-key"]);
    }

    [Fact]
    public async Task Create_session_serializes_the_wire_shape_the_beta_expects()
    {
        var (client, http) = Create();
        http.Routes["POST /v1/sessions"] = (HttpStatusCode.OK, """{"id":"sesn_1","status":"running"}""");

        var session = await client.CreateSessionAsync(new WireSessionCreate(
            "agent_1", "env_1", "condux fix",
            [new WireRepositoryResource(
                "https://github.com/acme/app", "ghs_token", new WireCheckout("branch", "main"))],
            [new WireUserMessage([new WireTextContent("fix it")])],
            new WireBudget(new WireMoney(ManagedAgentsClient.MinorUnits(5m), "USD"))));

        Assert.Equal("sesn_1", session.Id);

        var body = JsonDocument.Parse(http.Requests.Single().Body).RootElement;
        Assert.Equal("agent_1", body.GetProperty("agent").GetString());
        var resource = body.GetProperty("resources")[0];
        Assert.Equal("github_repository", resource.GetProperty("type").GetString());
        Assert.Equal("ghs_token", resource.GetProperty("authorization_token").GetString());
        Assert.Equal("branch", resource.GetProperty("checkout").GetProperty("type").GetString());
        var initial = body.GetProperty("initial_events")[0];
        Assert.Equal("user.message", initial.GetProperty("type").GetString());
        Assert.Equal("text", initial.GetProperty("content")[0].GetProperty("type").GetString());
        var budget = body.GetProperty("budget");
        Assert.Equal("limit", budget.GetProperty("type").GetString());
        Assert.Equal("500", budget.GetProperty("max_list_cost").GetProperty("amount").GetString());
    }

    [Fact]
    public async Task Session_usage_parses_the_live_beta_shape_including_actual_cost()
    {
        var (client, http) = Create();
        // Captured verbatim structure from the live probe: cache tokens dominate, list_cost is actual.
        http.Routes["GET /v1/sessions/sesn_1"] = (HttpStatusCode.OK, """
            {"id":"sesn_1","status":"idle","resources":[],
             "usage":{"input_tokens":4,"output_tokens":685,"cache_read_input_tokens":8153,
                      "cache_creation":{"ephemeral_1h_input_tokens":0,"ephemeral_5m_input_tokens":8398},
                      "list_cost":{"amount":"7","currency":"USD"}}}
            """);

        var session = await client.GetSessionAsync("sesn_1");

        Assert.Equal("idle", session.Status);
        Assert.Equal(4, session.Usage!.InputTokens);
        Assert.Equal(685, session.Usage.OutputTokens);
        Assert.Equal(8153, session.Usage.CacheReadInputTokens);
        Assert.Equal(8398, session.Usage.CacheCreation!.Ephemeral5mInputTokens);
    }

    [Fact]
    public async Task A_vendor_error_surfaces_the_message_and_a_429_is_recognizable()
    {
        var (client, http) = Create();
        http.Routes["GET /v1/sessions/sesn_x"] = (HttpStatusCode.TooManyRequests,
            """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""");

        var ex = await Assert.ThrowsAsync<ManagedAgentsApiException>(() => client.GetSessionAsync("sesn_x"));
        Assert.True(ex.IsRateLimit);
        Assert.Contains("slow down", ex.Message);
    }

    [Theory]
    [InlineData("5", "500")]
    [InlineData("5.00", "500")]
    [InlineData("0.07", "7")]
    [InlineData("12.999", "1299")] // never round a cap up
    public void Minor_units_convert_dollars_to_integer_cents(string usd, string expected) =>
        Assert.Equal(expected, ManagedAgentsClient.MinorUnits(decimal.Parse(usd)));

    [Fact]
    public void Options_require_the_api_key_and_validate_the_budget()
    {
        var missingKey = new ConfigurationBuilder().Build();
        Assert.Throws<InvalidOperationException>(() => ManagedAgentsOptions.FromEnv(missingKey));

        var bad = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CONDUX_ANTHROPIC_API_KEY"] = "k",
            ["CONDUX_CMA_MAX_SESSION_USD"] = "-1",
        }).Build();
        Assert.Throws<InvalidOperationException>(() => ManagedAgentsOptions.FromEnv(bad));

        var good = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CONDUX_ANTHROPIC_API_KEY"] = "k",
            ["CONDUX_CMA_MAX_SESSION_USD"] = "2.50",
            ["CONDUX_CMA_AGENT_ID"] = "agent_pin",
        }).Build();
        var options = ManagedAgentsOptions.FromEnv(good);
        Assert.Equal(2.50m, options.MaxSessionUsd);
        Assert.Equal("agent_pin", options.AgentId);
        Assert.Null(options.EnvironmentId);
    }
}
