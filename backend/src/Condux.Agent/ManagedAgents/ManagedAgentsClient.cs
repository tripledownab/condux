using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Condux.Agent.ManagedAgents;

/// <summary>The vendor rejected or throttled a call; carries the status so the gateway can treat a
/// 429 as "still running" on the poll path instead of failing the run.</summary>
public sealed class ManagedAgentsApiException(HttpStatusCode statusCode, string message)
    : InvalidOperationException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public bool IsRateLimit => StatusCode == HttpStatusCode.TooManyRequests;
}

/// <summary>
/// A thin HTTP layer over the Anthropic Managed Agents beta (ADR-0038) — the same plain-HttpClient
/// shape as <see cref="AnthropicMessagesClient"/>, no SDK. Every call carries the beta header; errors
/// surface as <see cref="ManagedAgentsApiException"/> carrying the status and the vendor's message,
/// reduced by <see cref="ModelApiError"/> and bounded by it. Retry/backoff is the caller's job (the fix
/// provider's poll interval is the backoff), and it keys on the status, never the text.
/// </summary>
public sealed class ManagedAgentsClient(HttpClient http, ManagedAgentsOptions options)
{
    private const string Beta = "managed-agents-2026-04-01";

    internal async Task<WireAgent> CreateAgentAsync(
        string name, string model, string system, CancellationToken ct = default) =>
        await PostAsync<WireAgent>("/v1/agents",
            new WireAgentCreate(name, model, system, [WireToolRef.AgentToolset]), ct);

    internal async Task<IReadOnlyList<WireAgent>> ListAgentsAsync(CancellationToken ct = default) =>
        (await GetAsync<WireAgentList>("/v1/agents", ct)).Data;

    internal async Task<WireEnvironment> CreateEnvironmentAsync(
        string name, CancellationToken ct = default) =>
        await PostAsync<WireEnvironment>("/v1/environments",
            new WireEnvironmentCreate(name, new WireEnvironmentConfig("cloud")), ct);

    internal async Task<IReadOnlyList<WireEnvironment>> ListEnvironmentsAsync(CancellationToken ct = default) =>
        (await GetAsync<WireEnvironmentList>("/v1/environments", ct)).Data;

    internal async Task<WireSession> CreateSessionAsync(
        WireSessionCreate request, CancellationToken ct = default) =>
        await PostAsync<WireSession>("/v1/sessions", request, ct);

    internal async Task<WireSession> GetSessionAsync(string sessionId, CancellationToken ct = default) =>
        await GetAsync<WireSession>($"/v1/sessions/{sessionId}", ct);

    /// <summary>All of a session's events, following pagination to the end — events are oldest-first
    /// and the caller needs the LAST agent message, so one page of a long agentic session would silently
    /// drop the answer. The page cap is a runaway bound, far above any real session.</summary>
    internal async Task<IReadOnlyList<WireEvent>> GetSessionEventsAsync(
        string sessionId, CancellationToken ct = default)
    {
        var events = new List<WireEvent>();
        string? page = null;
        for (var i = 0; i < 50; i++)
        {
            var path = $"/v1/sessions/{sessionId}/events?limit=100"
                + (page is null ? "" : $"&page={Uri.EscapeDataString(page)}");
            var list = await GetAsync<WireEventList>(path, ct);
            events.AddRange(list.Data);
            if (list.NextPage is null or "")
            {
                return events;
            }
            page = list.NextPage;
        }
        return events;
    }

    /// <summary>Best-effort cleanup of a finalized session; a failure is the caller's to swallow.</summary>
    internal async Task DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        using var request = NewRequest(HttpMethod.Delete, $"/v1/sessions/{sessionId}");
        using var response = await http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>Dollars to the wire's integer minor-unit amount ("100" = $1.00, probed live).</summary>
    internal static string MinorUnits(decimal usd) =>
        ((long)decimal.Round(usd * 100m, 0, MidpointRounding.ToZero)).ToString();

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Post, path);
        request.Content = new StringContent(
            JsonSerializer.Serialize(body, body.GetType()), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        return await ReadAsync<T>(response, ct);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{options.BaseUrl}{path}");
        request.Headers.Add("x-api-key", options.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Headers.Add("anthropic-beta", Beta);
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(ct)
            ?? throw new InvalidOperationException("Managed Agents API returned an empty response.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new ManagedAgentsApiException(response.StatusCode,
            $"Managed Agents API returned {(int)response.StatusCode}: {ModelApiError.Describe(detail)}");
    }
}
